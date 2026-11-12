using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamDownloadControl;

internal sealed class SteamInterface : IDisposable
{
    private const string AppManagerInterface = "IClientAppManager";
    private readonly nint module;
    private readonly PEReader image;
    private readonly SectionHeader code;
    private readonly SectionHeader readOnlyData;

    public SteamInterface(nint module, FileStream dll)
    {
        this.module = module;
        image = new PEReader(dll, PEStreamOptions.LeaveOpen);
        try
        {
            code = image.PEHeaders.SectionHeaders.FirstOrDefault(section => section.Name == ".text" &&
                (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0);
            readOnlyData = image.PEHeaders.SectionHeaders.FirstOrDefault(section => section.Name == ".rdata" &&
                (section.SectionCharacteristics & SectionCharacteristics.MemRead) != 0 &&
                (section.SectionCharacteristics & SectionCharacteristics.MemWrite) == 0);
            if (code.SizeOfRawData == 0 || readOnlyData.SizeOfRawData == 0)
                throw new InvalidOperationException("Die Steam-Bibliothek hat einen unerwarteten Aufbau.");
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public T GetEngineMethod<T>(nint engine, int slot) where T : Delegate
    {
        var entry = GetVtable(engine) + slot * IntPtr.Size;
        if (!Contains(readOnlyData, entry, IntPtr.Size))
            throw new InvalidOperationException("Steams Engine-Methodentabelle ist unvollständig.");
        var method = Marshal.ReadIntPtr(entry);
        if (!Contains(code, method, 1))
            throw new InvalidOperationException("Steams Engine-Methode liegt außerhalb der Steam-Bibliothek.");
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    public AppManagerMethods GetAppManagerMethods(nint manager)
    {
        var names = image.GetSectionData(readOnlyData.VirtualAddress).GetContent();
        var methods = new Dictionary<string, nint>(StringComparer.Ordinal);
        for (var entry = GetVtable(manager); Contains(readOnlyData, entry, IntPtr.Size); entry += IntPtr.Size)
        {
            var method = Marshal.ReadIntPtr(entry);
            if (!Contains(code, method, 1))
                break;

            var references = GetReferencedNames(method, names.AsSpan());
            if (!references.Remove(AppManagerInterface))
                continue;

            foreach (var name in references)
            {
                if (!methods.TryAdd(name, method) && methods[name] != method)
                    methods[name] = 0; // Ambiguous references cannot identify a method.
            }
        }
        return new AppManagerMethods(methods);
    }

    private HashSet<string> GetReferencedNames(nint method, ReadOnlySpan<byte> names)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        // On x64, the unwind table supplies the exact bounds of these non-leaf IPC wrappers.
        var function = RtlLookupFunctionEntry(method, out var imageBase, 0);
        var methodRva = (int)(method - module);
        if (function == 0 || imageBase != module || Marshal.ReadInt32(function) != methodRva)
            return result;
        var endRva = Marshal.ReadInt32(function, sizeof(int));
        if (endRva <= methodRva || !Contains(code, module + endRva - 1, 1))
            return result;

        var instructions = image.GetSectionData(methodRva).GetContent(0, endRva - methodRva).AsSpan();
        // The wrappers pass their interface and method names as RIP-relative C string addresses.
        const int leaSize = 7;
        for (var offset = 0; offset <= instructions.Length - leaSize; offset++)
        {
            if ((instructions[offset] & 0xF8) != 0x48 || instructions[offset + 1] != 0x8D ||
                (instructions[offset + 2] & 0xC7) != 0x05)
                continue;
            var displacement = BinaryPrimitives.ReadInt32LittleEndian(instructions[(offset + 3)..]);
            var nameOffset = (long)methodRva + offset + leaSize + displacement - readOnlyData.VirtualAddress;
            if (nameOffset < 0 || nameOffset >= names.Length)
                continue;
            var candidate = names.Slice((int)nameOffset, Math.Min(96, names.Length - (int)nameOffset));
            var end = candidate.IndexOf((byte)0);
            if (end > 0 && candidate[..end].IndexOfAnyExceptInRange((byte)'!', (byte)'~') < 0)
                result.Add(Encoding.ASCII.GetString(candidate[..end]));
        }
        return result;
    }

    private nint GetVtable(nint instance)
    {
        if (instance == 0 || !ReadProcessMemory(GetCurrentProcess(), instance, out var table, (nuint)IntPtr.Size, out _) ||
            !Contains(readOnlyData, table, IntPtr.Size))
            throw new InvalidOperationException("Steams native Methodentabelle konnte nicht geprüft werden.");
        return table;
    }

    private bool Contains(SectionHeader section, nint address, int size)
    {
        var offset = (long)(address - module) - section.VirtualAddress;
        return offset >= 0 && offset <= Math.Min(section.VirtualSize, section.SizeOfRawData) - size;
    }

    public void Dispose() => image.Dispose();

    internal sealed class AppManagerMethods(Dictionary<string, nint> methods)
    {
        public T Get<T>(string name) where T : Delegate
        {
            if (!methods.TryGetValue(name, out var method) || method == 0)
                throw new InvalidOperationException($"Steams Download-Schnittstelle konnte für {name} nicht bestätigt werden.");
            return Marshal.GetDelegateForFunctionPointer<T>(method);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint RtlLookupFunctionEntry(nint controlPc, out nint imageBase, nint historyTable);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, out nint value, nuint size, out nuint bytesRead);
}
