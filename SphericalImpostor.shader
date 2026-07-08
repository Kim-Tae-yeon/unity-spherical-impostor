// Custom Spherical Impostor (URP, Lit) - Amplify Impostors 매커니즘 재현
// 목적: yaw/pitch로 이미지(프레임) 선택 + roll 완벽 재현 + 노말맵 라이팅
// 원본: AmplifyImpostors/.../SphericalImpostorURP.shader 의 SphereImpostorVertex/Fragment
// 이 버전은 메인 라이트 + 앰비언트(SH) + 그림자 수신까지 지원. 패럴랙스/깊이출력은 제외.

Shader "Custom/Spherical Impostor (Lit)"
{
    Properties
    {
        [NoScaleOffset] _Albedo  ("Albedo Atlas (RGB) Alpha (A)", 2D) = "white" {}
        [NoScaleOffset] _Normals ("Normal Atlas (RGB) Depth (A)", 2D) = "bump" {}
        _ClipMask ("Clip Mask", Range(0,1)) = 0.5
        _TextureBias ("Texture Bias", Float) = -1
        _FramesX ("Frames X (yaw)", Float) = 8
        _FramesY ("Frames Y (pitch)", Float) = 8
        // 일반 Quad 사용 시 ON: Amplify가 메시에 굽는 실루엣 pixelOffset을 셰이더에서 재구성.
        // Amplify 생성 메시를 쓰면 OFF (이미 메시에 들어있어 이중 적용 방지).
        [Toggle(_RECONSTRUCT_PIVOT)] _ReconstructPivot ("Reconstruct Pivot (plain quad)", Float) = 1
        _ImpostorSize ("Impostor Size (= quad vertex range)", Float) = 1
        _Offset ("Pivot Offset (bounds center)", Vector) = (0,0,0,0)
        // Amplify 베이커의 _AI_SizeOffset 값을 그대로 입력. .zw = 실루엣 UV 재중심 오프셋(비대칭 메시일 때 0이 아님)
        _AI_SizeOffset ("Size & Offset (from Amplify)", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "TransparentCutout"
            "Queue"          = "AlphaTest"
        }

        Cull Back
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma shader_feature_local _RECONSTRUCT_PIVOT

            // 라이팅 키워드 (메인 라이트 그림자 + 소프트 그림자)
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // 원본 AI 매크로 대응
            #define AI_ObjectToWorld GetObjectToWorldMatrix()
            #define AI_WorldToObject GetWorldToObjectMatrix()
            #define AI_INV_TWO_PI    INV_TWO_PI
            #define AI_PI            PI
            #define AI_INV_PI        INV_PI

            TEXTURE2D(_Albedo);   SAMPLER(sampler_Albedo);
            TEXTURE2D(_Normals);  SAMPLER(sampler_Normals);

            CBUFFER_START(UnityPerMaterial)
                float  _FramesX;
                float  _FramesY;
                float  _ImpostorSize;
                float  _ClipMask;
                float  _TextureBias;
                float4 _Offset;
                float4 _AI_SizeOffset;
            CBUFFER_END

            struct Attributes
            {
                // 빌보드 쿼드: positionOS.xy 가 [-0.5,0.5]*ImpostorSize 범위의 확장(uvExpansion)으로 쓰인다.
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 frameUV    : TEXCOORD1; // 선택된 프레임의 아틀라스 UV
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                // ---- 상수/분수 준비 ----
                float2 uvOffset       = _AI_SizeOffset.zw; // 실루엣 UV 재중심 보정(Amplify 호환)
                float sizeX           = _FramesX;         // yaw 프레임 수
                float sizeY           = _FramesY - 1;     // pitch 축(원본과 동일하게 -1 보정)
                float UVscale         = _ImpostorSize;
                float4 fractions      = 1.0 / float4(sizeX, _FramesY, sizeY, UVscale);
                float2 sizeFraction   = fractions.xy;     // (1/FramesX, 1/FramesY)
                float axisSizeFraction= fractions.z;      // 1/(FramesY-1)
                float fractionsUVscale= fractions.w;      // 1/ImpostorSize

                // ---- 오브젝트 공간 카메라 방향 ----
                float3 worldCameraPos;
                if (UNITY_MATRIX_P[3][3] == 1) // Orthographic
                    worldCameraPos = AI_ObjectToWorld._m03_m13_m23 + UNITY_MATRIX_I_V._m02_m12_m22 * 5000;
                else                            // Perspective
                    worldCameraPos = GetCameraRelativePositionWS(_WorldSpaceCameraPos);

                float3 objectCameraPosition = mul(AI_WorldToObject, float4(worldCameraPos, 1)).xyz - _Offset.xyz;
                float3 dir = normalize(objectCameraPosition); // 카메라를 향하는 방향

                // ---- 빌보드 기저(수평/수직 벡터) ----
                float3 up   = float3(0, 1, 0);
                float3 hori = normalize(cross(dir, up));
                float3 vertV= cross(hori, dir);

                // ---- (1) YAW → 열 선택용 각도 ----
                float verticalAngle = frac(atan2(-dir.z, -dir.x) * AI_INV_TWO_PI) * sizeX + 0.5;

                // ---- (2) PITCH → 행 선택용 각도 ----
                float verticalDot = dot(dir, up);                               // = sin(pitch)
                float upAngle     = acos(-verticalDot) * AI_INV_PI + axisSizeFraction * 0.5;

                // ---- (3) ROLL 재현: 이산 yaw 프레임과 연속 빌보드의 각도 오차 보정 ----
                //   보정량 ∝ sin(pitch) (극점에서 최대, 적도에서 0) × 두 열 사이 보간위치(2*frac-1)
                float yRot = sizeFraction.x * AI_PI * verticalDot * (2 * frac(verticalAngle) - 1);
                float cy = cos(yRot);
                float sy = sin(yRot);

                float2 uvExpansion = IN.positionOS.xy;
                // ---- 피벗 보정 ②: 실루엣 pixelOffset 재구성 (일반 Quad용) ----
                //   Amplify는 이 오프셋을 메시 정점에 굽는다. 일반 Quad엔 없으므로 여기서 복원.
                //   frameUV의 -uvOffset 항이 이 오프셋의 UV 기여분을 정확히 상쇄한다.
                #if defined(_RECONSTRUCT_PIVOT)
                    uvExpansion += _AI_SizeOffset.zw * _ImpostorSize * float2(_FramesX, _FramesY);
                #endif
                float2 uvRotator   = mul(uvExpansion, float2x2(cy, -sy, sy, cy)); // 빌보드 UV를 roll 만큼 회전
                float3 billboard   = hori * uvRotator.x + vertV * uvRotator.y;

                // ---- (4) 이미지 선택: 격자 좌표 → 아틀라스 UV ----
                float2 relativeCoords = float2(floor(verticalAngle),
                                               min(floor(upAngle * sizeY), sizeY));
                OUT.frameUV = ((uvExpansion * fractionsUVscale + 0.5) + relativeCoords) * sizeFraction - uvOffset;

                // ---- 출력 ----
                float3 positionOS = billboard + _Offset.xyz;
                OUT.positionWS = TransformObjectToWorld(positionOS);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // ---- 알베도 + 알파 클립 ----
                half4 albedo = SAMPLE_TEXTURE2D_BIAS(_Albedo, sampler_Albedo, IN.frameUV, _TextureBias);
                clip(albedo.a - _ClipMask);

                // ---- 노말: Amplify는 오브젝트 공간에 구움 → 월드 공간으로 변환 ----
                float4 normalSample = SAMPLE_TEXTURE2D_BIAS(_Normals, sampler_Normals, IN.frameUV, _TextureBias);
                float3 objectNormal = normalSample.xyz * 2.0 - 1.0;
                float3 worldNormal  = normalize(mul((float3x3)AI_ObjectToWorld, objectNormal));

                // ---- 라이팅: 메인 라이트(그림자) + 앰비언트(SH) ----
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif
                Light mainLight = GetMainLight(shadowCoord);

                half3 lighting = mainLight.color * (saturate(dot(worldNormal, mainLight.direction))
                                                    * mainLight.shadowAttenuation);
                half3 ambient  = SampleSH(worldNormal);

                // ---- 추가 라이트(포인트/스팟) ----
                #if defined(_ADDITIONAL_LIGHTS)
                    uint count = GetAdditionalLightsCount();
                    for (uint li = 0; li < count; li++)
                    {
                        Light L = GetAdditionalLight(li, IN.positionWS);
                        lighting += L.color * (saturate(dot(worldNormal, L.direction))
                                               * L.distanceAttenuation * L.shadowAttenuation);
                    }
                #endif

                half3 color = albedo.rgb * (lighting + ambient);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
