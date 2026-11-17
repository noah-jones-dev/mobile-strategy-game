using UnityEngine;

namespace Hexfront
{
    /// <summary>
    /// Entry point. Unity calls this automatically after the first scene loads, so the game works
    /// from any scene (even an empty one): everything is created from code.
    /// </summary>
    public static class Bootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            if (App.Instance != null) return;
            var go = new GameObject("HEXFRONT");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<App>();
        }
    }
}
