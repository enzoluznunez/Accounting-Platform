using System.Runtime.InteropServices;
using AOT;
using UnityEngine.Events;

public static class Voip {

    public static UnityAction<byte[]> OnAudioCaptured;

#if UNITY_ANDROID && !UNITY_EDITOR

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioCallback(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        byte[] data,
        int numFrames
    );

    [MonoPInvokeCallback(typeof(AudioCallback))]
    private static void HandleAudio(byte[] data, int numFrames) {
        OnAudioCaptured?.Invoke(data);
    }

    private static AudioCallback callback = HandleAudio;

    [DllImport("voip", EntryPoint = "init")]
    private static extern void Init(AudioCallback callbackParam);

    public static void Init() {
        Init(callback);
    }

    [DllImport("voip", EntryPoint = "destroy")]
    public static extern void Destroy();

    [DllImport("voip", EntryPoint = "start")]
    public static extern void Start();

    [DllImport("voip", EntryPoint = "stop")]
    public static extern void Stop();

#else

    public static void Init() { }
    public static void Destroy() { }
    public static void Start() { }
    public static void Stop() { }

#endif

}
