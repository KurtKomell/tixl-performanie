RWStructuredBuffer<uint> BucketPrefixSum :register(u0); 
RWStructuredBuffer<uint> BucketIndices :register(u1);   

RWStructuredBuffer<uint> BucketOffsetSum :register(u2);   
RWStructuredBuffer<uint2> SortedIndices :register(u3);  

cbuffer Params : register(b0)
{
    int BucketCount;
    int ParticleCount;
}

// 5. WriteSortedIndices.compute
// Writes original index into SortedIndices using atomic offset per bucket
[numthreads(THREADS_PER_GROUP, 1, 1)]
void WriteSortedIndices(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ParticleCount) return;
    //if (isnan(GPoints[id.x].Scale.x)) return;

    uint bucket = BucketIndices[id.x];
    uint offset;
    InterlockedAdd(BucketOffsetSum[bucket], 1, offset);
    SortedIndices[BucketPrefixSum[bucket] + offset] = id.x;
}
