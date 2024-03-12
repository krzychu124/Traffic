using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Traffic.Helpers
{
    public static class JobExtensions
    {
        public static unsafe JobHandle Schedule<T, TData>(this T jobData, ref NativeArray<TData> forEachCount, int innerloopBatchCount, JobHandle dependsOn = new JobHandle())
            where T : struct, IJobParallelForDefer
            where TData : struct {
            return IJobParallelForDeferExtensions.Schedule(jobData, (int*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks<TData>(forEachCount), innerloopBatchCount, dependsOn);
        }
    }
}
