using System.Threading;

namespace NexusDownloader.Download
{
    public class AdaptiveLimiter
    {
        private readonly SemaphoreSlim _semaphore;

        private int _target;
        private readonly int _min;
        private readonly int _max;

        public AdaptiveLimiter(int initial, int min, int max)
        {
            _semaphore = new SemaphoreSlim(initial);
            _target = initial;
            _min = min;
            _max = max;
        }

        public SemaphoreSlim Semaphore => _semaphore;

        public void Update(int delay)
        {
            int newTarget = _target;

            if (delay > 220)
                newTarget -= 3;
            else if (delay > 160)
                newTarget -= 2;
            else if (delay > 120)
                newTarget -= 1;
            else if (delay < 40)
                newTarget += 1;

            newTarget = System.Math.Clamp(newTarget, _min, _max);

            int diff = newTarget - _target;
            _target = newTarget;

            if (diff > 0)
            {
                _semaphore.Release(diff);
            }
            else if (diff < 0)
            {
                for (int i = 0; i < -diff; i++)
                    _ = _semaphore.Wait(0);
            }
        }
    }
}