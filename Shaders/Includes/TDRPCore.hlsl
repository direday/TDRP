#ifndef TDRP_CORE_INCLUDED
#define TDRP_CORE_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

// Per-draw built-ins. Block layout matters to the SRP Batcher: the Space block must come
// first, and each block (Space, Motion Vectors) must be declared complete and contiguous.
//
// Under DOTS instancing (BatchRendererGroup) this cbuffer does not exist: per-instance
// values come from the BRG's raw buffer through the metadata cbuffer declared below.
#ifndef DOTS_INSTANCING_ON
CBUFFER_START(UnityPerDraw)
	// Space block
	float4x4 unity_ObjectToWorld;
	float4x4 unity_WorldToObject;
	float4 unity_LODFade;
	real4 unity_WorldTransformParams;

	// Motion-vector block (bound for draws requesting PerObjectData.MotionVectors)
	float4x4 unity_MatrixPreviousM;
	float4x4 unity_MatrixPreviousMI;
	float4 unity_MotionVectorsParams;
CBUFFER_END
#endif

float4x4 unity_MatrixV;
float4x4 unity_MatrixInvV;
float4x4 unity_MatrixVP;
float4x4 unity_MatrixInvVP;
float4x4 glstate_matrix_projection;
float4x4 unity_MatrixInvP;

float3 _WorldSpaceCameraPos;

#define UNITY_MATRIX_M     unity_ObjectToWorld
#define UNITY_MATRIX_I_M   unity_WorldToObject
#define UNITY_MATRIX_V     unity_MatrixV
#define UNITY_MATRIX_I_V   unity_MatrixInvV
#define UNITY_MATRIX_P     glstate_matrix_projection
#define UNITY_MATRIX_I_P   unity_MatrixInvP
#define UNITY_MATRIX_VP    unity_MatrixVP
#define UNITY_MATRIX_I_VP  unity_MatrixInvVP
#define UNITY_PREV_MATRIX_M   unity_MatrixPreviousM
#define UNITY_PREV_MATRIX_I_M  unity_MatrixPreviousMI

// Must come after the UNITY_MATRIX_* defines above (it redefines the model matrices for
// instanced variants) and before SpaceTransforms.hlsl (which consumes them).
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"

#ifdef UNITY_DOTS_INSTANCING_ENABLED
// Built-ins a BatchRendererGroup may supply per instance. unity_ObjectToWorld and
// unity_WorldToObject are required; the previous-frame pair is optional (see
// GetMotionPrevObjectToWorldMatrix). Matrices are packed float3x4, as BRG expects.
UNITY_DOTS_INSTANCING_START(BuiltinPropertyMetadata)
	UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_ObjectToWorld)
	UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_WorldToObject)
	UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_MatrixPreviousM)
	UNITY_DOTS_INSTANCED_PROP_OVERRIDE_SUPPORTED(float3x4, unity_MatrixPreviousMI)
UNITY_DOTS_INSTANCING_END(BuiltinPropertyMetadata)

// The rest of the per-draw block is derived from BRG's per-draw visibility flags.
#define unity_LODFade               LoadDOTSInstancedData_LODFade()
#define unity_WorldTransformParams  LoadDOTSInstancedData_WorldTransformParams()
#define unity_MotionVectorsParams   LoadDOTSInstancedData_MotionVectorsParams()

// Hooks expanded by UNITY_SETUP_INSTANCE_ID. TDRP reads no per-object SH (its ambient is
// global) and no renderer bounds, so both are no-ops.
#define UNITY_SETUP_DOTS_SH_COEFFS
#define UNITY_SETUP_DOTS_RENDER_BOUNDS
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"

// Previous-frame model matrix for per-object motion vectors. Unlike core's
// GetPrevObjectToWorldMatrix, it tolerates a BRG that does not supply unity_MatrixPreviousM
// (metadata 0): that would read BRG's zero header as a matrix and smear the object across the
// screen, so fall back to the current matrix instead, i.e. camera-only motion.
float4x4 GetMotionPrevObjectToWorldMatrix()
{
#ifdef UNITY_DOTS_INSTANCING_ENABLED
	return UNITY_DOTS_INSTANCED_METADATA_NAME(float3x4, unity_MatrixPreviousM) != 0
		? UNITY_PREV_MATRIX_M : UNITY_MATRIX_M;
#else
	return UNITY_PREV_MATRIX_M;
#endif
}

#endif
