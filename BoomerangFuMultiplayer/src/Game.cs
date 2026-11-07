using UnityEngine;

namespace BoomerangFuMultiplayer
{
    /// <summary>
    /// Cheap access to the game's managers. <c>Singleton&lt;T&gt;.Instance</c> runs FindObjectOfType and logs an
    /// error (with a stack trace) on every call while the manager does not exist yet, e.g. during the
    /// startup scene load. The mod polls managers every frame, which slowed that load to a crawl
    /// (minutes on Windows, forever under Wine). Here a missing manager is probed at most twice a second.
    /// </summary>
    internal static class Game
    {
        static class Probe<T> { public static float Next; }

        public static T Get<T>() where T : MonoBehaviour
        {
            if (Singleton<T>.InstanceExists) return Singleton<T>.Instance;
            float now = Time.unscaledTime;
            if (now < Probe<T>.Next) return null;
            Probe<T>.Next = now + 0.5f;
            return Object.FindObjectOfType<T>() != null ? Singleton<T>.Instance : null; // caches it
        }
    }
}
