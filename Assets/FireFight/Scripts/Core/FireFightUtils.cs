using UnityEngine;
using FireFight.Managers;

namespace FireFight.Core
{
    /// <summary>
    /// Shared world-space bounds computation.
    ///
    /// Several scripts (Flammable, SeedIgniter, FlammableAutoTagger, NPCFireTarget)
    /// each used to hand-roll the same "prefer a collider, else fall back to a
    /// renderer" logic to size an object's fuel/ignition volume. That duplication is
    /// now consolidated here so the rule lives in exactly one place.
    /// </summary>
    public static class BoundsUtil
    {
        /// <summary>
        /// World-space bounds of an object, preferring its collider over its renderer.
        /// Returns false when the object has neither (no sensible volume to use).
        /// </summary>
        /// <param name="root">The object to measure (searched including children).</param>
        /// <param name="bounds">The resulting world-space volume.</param>
        /// <param name="requireSolidCollider">
        /// When true, only an enabled, non-trigger collider counts (used by gameplay
        /// queries that must ignore trigger volumes). When false, any collider is
        /// accepted and a renderer is used as a fallback (used when painting fuel).
        /// </param>
        public static bool TryGetWorldBounds(Component root, out Bounds bounds,
            bool requireSolidCollider = false)
        {
            bounds = default;
            if (root == null) return false;

            Collider col = root.GetComponentInChildren<Collider>();
            if (col != null)
            {
                if (requireSolidCollider && (!col.enabled || col.isTrigger)) return false;
                bounds = col.bounds;
                return true;
            }

            // No collider. A renderer still gives a usable volume unless the caller
            // demands a solid collider (gameplay line-of-fire checks).
            if (requireSolidCollider) return false;

            Renderer rend = root.GetComponentInChildren<Renderer>();
            if (rend != null) { bounds = rend.bounds; return true; }

            return false;
        }

        /// <summary>
        /// Combined world-space bounds of every renderer on an object. Use when a
        /// multi-part model has no single collider that covers the whole thing.
        /// Returns false if the object has no renderers.
        /// </summary>
        public static bool TryGetCombinedRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (root == null) return false;

            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return false;

            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return true;
        }
    }

    /// <summary>
    /// Resolves the scene's <see cref="FireManager"/>.
    ///
    /// Many scripts kept a serialized FireManager reference with a
    /// "if null, FindFirstObjectByType&lt;FireManager&gt;()" fallback. That fallback
    /// is centralised here so the lookup (and any future caching) lives in one place.
    /// </summary>
    public static class FireManagerLocator
    {
        /// <summary>
        /// Return <paramref name="assigned"/> if it is set, otherwise find a
        /// FireManager in the loaded scenes. May return null if none exists.
        /// </summary>
        public static FireManager Resolve(FireManager assigned)
        {
            return assigned != null ? assigned : Object.FindFirstObjectByType<FireManager>();
        }
    }

    /// <summary>
    /// Center-screen (crosshair) pick helper. Several command/targeting scripts cast
    /// a ray through the viewport centre to find what the player is aiming at; the
    /// ray construction is shared here.
    /// </summary>
    public static class ScreenPick
    {
        private static readonly Vector3 ViewportCentre = new Vector3(0.5f, 0.5f, 0f);

        /// <summary>A ray through the centre of the given camera's viewport.</summary>
        public static Ray CentreRay(Camera camera) => camera.ViewportPointToRay(ViewportCentre);

        /// <summary>
        /// Raycast through the crosshair. Returns true and the hit on success.
        /// Triggers are ignored to match the previous per-script behaviour.
        /// </summary>
        public static bool RaycastCentre(Camera camera, float distance, LayerMask layers,
            out RaycastHit hit)
        {
            hit = default;
            if (camera == null) return false;
            return Physics.Raycast(CentreRay(camera), out hit, distance, layers,
                QueryTriggerInteraction.Ignore);
        }
    }
}
