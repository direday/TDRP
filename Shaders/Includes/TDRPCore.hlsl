#ifndef TDRP_CORE_INCLUDED
#define TDRP_CORE_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

CBUFFER_START(UnityPerDraw)
	float4x4 unity_ObjectToWorld;
	float4x4 unity_WorldToObject;
	float4 unity_LODFade;
	real4 unity_WorldTransformParams;
CBUFFER_END

float4x4 unity_MatrixV;
float4x4 unity_MatrixInvV;
float4x4 unity_MatrixVP;
float4x4 unity_MatrixInvVP;
float4x4 glstate_matrix_projection;
float4x4 unity_MatrixInvP;

float4x4 unity_MatrixPreviousM;
float4x4 unity_MatrixPreviousMI;

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

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"

#endif
