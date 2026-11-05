using System.Runtime.InteropServices;

public static class Speaker {

#if UNITY_ANDROID && !UNITY_EDITOR

    [DllImport("spkr", EntryPoint = "init")]
    public static extern void Init();

    [DllImport("spkr", EntryPoint = "destroy")]
    public static extern void Destroy();

    [DllImport("spkr", EntryPoint = "start")]
    public static extern void Start();

    [DllImport("spkr", EntryPoint = "stop")]
    public static extern void Stop();

    [DllImport("spkr", EntryPoint = "clear")]
    public static extern void Clear();

    [DllImport("spkr", EntryPoint = "play")]
    private static extern void Play(
        [MarshalAs(UnmanagedType.LPArray)]
        byte[] data,
        int arrLength
    );

    public static void Write(byte[] data) {
        Play(data, data.Length);
    }

    public static void Flush() {
        Stop();
        Clear();
        Start();
    }

#else

    public static void Init() { }
    public static void Destroy() { }
    public static void Start() { }
    public static void Stop() { }
    public static void Clear() { }
    public static void Write(byte[] data) { }
    public static void Flush() { }

#endif

}
