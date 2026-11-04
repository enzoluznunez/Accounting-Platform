using System.Runtime.InteropServices;
using AOT;
using UnityEngine.Events;

public static class Voip {

    public static UnityAction<byte[]> turret;

#if UNITY_ANDROID && !UNITY_EDITOR

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void fnPtr(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        byte[] data,
        int numFrames
    );

    [MonoPInvokeCallback(typeof(fnPtr))]
    private static void callbackDef(byte[] data, int numFrames) {
        turret?.Invoke(data);
    }

    private static fnPtr callback = callbackDef;

    [DllImport("voip", EntryPoint = "init")]
    private static extern void init(fnPtr callbackParam);

    public static void init() {
        init(callback);
    }

    [DllImport("voip", EntryPoint = "destroy")]
    public static extern void destroy();

    [DllImport("voip", EntryPoint = "start")]
    public static extern void start();

    [DllImport("voip", EntryPoint = "stop")]
    public static extern void stop();

#else

    public static void init() { }
    public static void destroy() { }
    public static void start() { }
    public static void stop() { }

#endif

}
