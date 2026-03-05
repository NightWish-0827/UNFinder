using UnityEngine;

// The Tracker component is not visible to the user.
[AddComponentMenu("")] 
public class UNTracker : MonoBehaviour
{
    internal int hash;
    private bool _isRegistered = false;

    // Method to initialize the tracker.
    internal void Initialize(int hash)
    {
        if (_isRegistered)
        {
            if (this.hash != hash)
            {
                int oldHash = this.hash;
                this.hash = hash;
                UN.RemoveFromCache(oldHash, gameObject);
                UN.RegisterToCache(this.hash, gameObject);
            }
            return;
        }

        this.hash = hash;
        if (!_isRegistered)
        {
            UN.RegisterToCache(this.hash, gameObject);
            _isRegistered = true;
        }
    }

    // Method to remove the tracker from the cache.
    private void OnDestroy()
    {
        if (_isRegistered)
        {
            UN.RemoveFromCache(hash, gameObject);
            _isRegistered = false;
        }
    }
}