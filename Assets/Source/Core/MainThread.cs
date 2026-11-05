using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public class MainThread : MonoBehaviour {

    private static MainThread _instance;
    private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap() {
        if (_instance != null) return;
        var go = new GameObject("MainThread");
        _instance = go.AddComponent<MainThread>();
        DontDestroyOnLoad(go);
    }

    public static Task Run(Action action) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() => {
            try {
                action();
                tcs.SetResult(true);
            }
            catch (Exception e) {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    private void Update() {
        while (_queue.TryDequeue(out var action)) action();
    }
}
