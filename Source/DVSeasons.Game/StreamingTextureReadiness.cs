using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    /// <summary>
    /// Acquires the mip level needed for a CPU readback of a streamed game texture.
    /// Unity's streaming system is asynchronous, so a wall-clock delay cannot prove
    /// that the requested pixels are resident. The lease keeps the request active
    /// until the GPU copy has completed, then restores the game's original request.
    /// </summary>
    internal static class StreamingTextureReadiness
    {
        private sealed class PendingRequest
        {
            public Texture2D Texture;
            public int PreviousLevel;
            public int RequestedLevel;
            public bool HasRequest;
        }

        private static readonly Dictionary<int, PendingRequest> pendingRequests =
            new Dictionary<int, PendingRequest>();
        private static readonly HashSet<int> warningTextureIds = new HashSet<int>();

        internal sealed class ReadLease : IDisposable
        {
            private readonly Texture2D texture;
            private readonly int previousLevel;
            private readonly int requestedLevel;
            private bool disposed;

            public ReadLease(Texture2D texture, int previousLevel, int requestedLevel)
            {
                this.texture = texture;
                this.previousLevel = previousLevel;
                this.requestedLevel = requestedLevel;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (texture == null) return;
                try
                {
                    // Do not overwrite a request made by another owner while the
                    // synchronous GPU readback was in progress. In normal use the
                    // value is still ours; the comparison makes cleanup safe when
                    // a scene unload or another streaming policy races this lease.
                    if (texture.requestedMipmapLevel == requestedLevel)
                    {
                        if (previousLevel < 0) texture.ClearRequestedMipmapLevel();
                        else texture.requestedMipmapLevel = previousLevel;
                    }
                }
                catch (Exception exception)
                {
                    WarnOnce(texture, "[DVSeasons] Could not restore a streamed texture mip request: ",
                        exception);
                }
            }
        }

        /// <summary>
        /// Returns false while Unity is still loading the requested mip. No texture
        /// pixels may be read by the caller until this returns true.
        /// </summary>
        public static bool TryAcquire(Texture2D texture, int targetWidth, int targetHeight,
            out ReadLease lease)
        {
            lease = null;
            if (texture == null) return false;
            try
            {
                if (!texture.streamingMipmaps || texture.mipmapCount <= 1)
                {
                    Cancel(texture);
                    return true;
                }

                var textureId = texture.GetInstanceID();
                PendingRequest pending;
                if (!pendingRequests.TryGetValue(textureId, out pending) ||
                    !object.ReferenceEquals(pending.Texture, texture))
                {
                    if (pending != null)
                    {
                        pendingRequests.Remove(textureId);
                        warningTextureIds.Remove(textureId);
                        RestorePendingRequest(pending);
                    }
                    pending = new PendingRequest
                    {
                        Texture = texture,
                        PreviousLevel = texture.requestedMipmapLevel,
                        RequestedLevel = -1
                    };
                    pendingRequests.Add(textureId, pending);
                }
                var previousLevel = pending.PreviousLevel;
                var requestedLevel = SelectMipmapLevel(texture, targetWidth, targetHeight);
                pending.RequestedLevel = requestedLevel;
                pending.HasRequest = true;
                if (texture.requestedMipmapLevel != requestedLevel)
                    texture.requestedMipmapLevel = requestedLevel;

                // This is Unity's completion signal for the exact requested mip;
                // loadedMipmapLevel alone can describe a previous request while a
                // newer stream operation is still pending.
                if (!texture.IsRequestedMipmapLevelLoaded()) return false;
                // A memory budget or an importer minimum can leave a coarser mip
                // resident while Unity reports the request as complete. It is not
                // sufficient for our target-sized readback, so keep polling until
                // the loaded level is at least as detailed as the requested one.
                var loadedLevel = texture.loadedMipmapLevel;
                if (loadedLevel < 0 || loadedLevel > requestedLevel) return false;
                pendingRequests.Remove(textureId);
                lease = new ReadLease(texture, previousLevel, requestedLevel);
                return true;
            }
            catch (Exception exception)
            {
                // A failed readiness query is unsafe to treat as ready. The next
                // frame retries after Unity has finished changing the texture.
                var textureId = 0;
                try { textureId = texture.GetInstanceID(); } catch { }
                if (textureId == 0 || warningTextureIds.Add(textureId))
                    Debug.LogWarning("[DVSeasons] Waiting for streamed texture: " + exception.Message);
                return false;
            }
        }

        public static void Cancel(Texture2D texture)
        {
            if (texture == null)
            {
                // Unity overloads == for destroyed objects. Keep the managed
                // reference searchable so a destroyed streamed asset cannot leave
                // a stale instance-id entry in the table until the next world.
                var destroyedIds = new List<int>();
                foreach (var pair in pendingRequests)
                    if (object.ReferenceEquals(pair.Value.Texture, texture)) destroyedIds.Add(pair.Key);
                for (var i = 0; i < destroyedIds.Count; i++)
                {
                    pendingRequests.Remove(destroyedIds[i]);
                    warningTextureIds.Remove(destroyedIds[i]);
                }
                return;
            }
            var textureId = 0;
            try { textureId = texture.GetInstanceID(); }
            catch { return; }
            PendingRequest pending;
            if (!pendingRequests.TryGetValue(textureId, out pending)) return;
            pendingRequests.Remove(textureId);
            warningTextureIds.Remove(textureId);
            RestorePendingRequest(pending);
        }

        private static void RestorePendingRequest(PendingRequest pending)
        {
            if (pending == null || pending.Texture == null) return;
            // The request may have been changed by another system after our
            // last poll. Preserve that newer request instead of clobbering it.
            try
            {
                if (pending.HasRequest &&
                    pending.Texture.requestedMipmapLevel == pending.RequestedLevel)
                {
                    if (pending.PreviousLevel < 0) pending.Texture.ClearRequestedMipmapLevel();
                    else pending.Texture.requestedMipmapLevel = pending.PreviousLevel;
                }
            }
            catch (Exception exception)
            {
                var id = 0;
                try { id = pending.Texture.GetInstanceID(); } catch { }
                if (id == 0 || warningTextureIds.Add(id))
                    Debug.LogWarning("[DVSeasons] Could not restore a streamed texture mip request: " +
                        exception.Message);
            }
            }

        public static void Reset()
        {
            foreach (var pending in pendingRequests.Values)
                RestorePendingRequest(pending);
            pendingRequests.Clear();
            warningTextureIds.Clear();
        }

        private static void WarnOnce(Texture2D texture, string prefix, Exception exception)
        {
            var id = 0;
            try { if (texture != null) id = texture.GetInstanceID(); } catch { }
            if (id == 0 || warningTextureIds.Add(id))
                Debug.LogWarning(prefix + exception.Message);
        }

        internal static int SelectMipmapLevel(Texture2D texture, int targetWidth, int targetHeight)
        {
            if (texture == null || texture.mipmapCount <= 1) return 0;
            var sourceWidth = Mathf.Max(1, texture.width);
            var sourceHeight = Mathf.Max(1, texture.height);
            var requestedWidth = Mathf.Max(1, targetWidth);
            var requestedHeight = Mathf.Max(1, targetHeight);
            var ratio = Mathf.Max(sourceWidth / (float)requestedWidth,
                sourceHeight / (float)requestedHeight);
            var level = ratio <= 1f ? 0 : Mathf.FloorToInt(Mathf.Log(ratio, 2f));
            // Do not clamp to minimumMipmapLevel here. An explicit requested level
            // is independent of that quality limit; if the memory budget still
            // leaves a coarser mip resident, TryAcquire keeps polling instead of
            // accepting a blurry readback as if it were complete.
            return Mathf.Clamp(level, 0, texture.mipmapCount - 1);
        }
    }
}
