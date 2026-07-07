namespace TDRP
{
	// Forward+ cluster grid dimensions. These MUST stay in sync with the matching
	// #defines in Shaders/Includes/Clustering.hlsl — the C# side allocates the GPU
	// buffers from these counts and the shaders index them with the same layout.
	public static class ClusterConfig
	{
		public const int ClusterX = 16;
		public const int ClusterY = 9;
		public const int ClusterZ = 24;
		public const int ClusterCount = ClusterX * ClusterY * ClusterZ;
		public const int MaxLightsPerCluster = 64;

		public const int LightIndexCount = ClusterCount * MaxLightsPerCluster;
	}
}
