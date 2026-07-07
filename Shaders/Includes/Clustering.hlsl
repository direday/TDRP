#ifndef TDRP_CLUSTERING_INCLUDED
#define TDRP_CLUSTERING_INCLUDED

// Forward+ cluster grid. The frustum is split into CLUSTER_X * CLUSTER_Y screen
// tiles and CLUSTER_Z exponential depth slices. A compute pass culls lights into
// each cluster so the fragment shader only loops the lights that can reach it.
//
// IMPORTANT: these MUST stay in sync with ClusterConfig.cs on the C# side
// (buffer sizes are allocated from those constants).
#define CLUSTER_X 16
#define CLUSTER_Y 9
#define CLUSTER_Z 24
#define CLUSTER_COUNT (CLUSTER_X * CLUSTER_Y * CLUSTER_Z)
#define MAX_LIGHTS_PER_CLUSTER 64

// Per-light record. Layout MUST match the C# `PunctualLightData` struct (all
// float4 so the structured-buffer packing is identical on both sides).
struct PunctualLightData
{
	float4 positionRange; // xyz: world position, w: 1 / range^2
	float4 color;         // rgb: color * intensity (linear), w: spot cone offset
	float4 spotDirScale;  // xyz: spot direction (surface->light), w: spot cone scale
};

// Linear index of cluster (x,y,z). Z is the outer dimension so that all clusters
// of one screen tile are contiguous-ish in Z; any consistent scheme works as long
// as the compute writer and the shader reader agree.
uint ClusterLinearIndex(uint3 c)
{
	return (c.z * CLUSTER_Y + c.y) * CLUSTER_X + c.x;
}

#endif
