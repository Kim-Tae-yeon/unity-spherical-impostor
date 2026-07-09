# Spherical Impostor — 완전 재구현 명세서

이 문서 하나로 **아무 것도 없는 상태에서** Spherical 임포스터(베이킹 + 런타임 셰이더 + 메시)를
처음부터 다시 구현할 수 있도록 모든 수식·좌표규약·데이터 배치·유도 과정을 담았습니다.
(엔진/언어 무관. HLSL·Unity 예시를 쓰지만 원리는 동일.)

---

## 0. 좌표 규약 (먼저 고정)

- **오브젝트 공간(object space)** 기준으로 전부 계산한다. (임포스터 GameObject의 로컬 좌표)
- **up = (0, 1, 0)** (Y-up). Unity 왼손 좌표, Y 위, Z 앞.
- `dir` = **피벗에서 카메라로 향하는 단위 벡터** (오브젝트 공간). 시선의 반대방향.
- 각도 단위: 내부 계산은 라디안. 표에는 도(°) 병기.
- 프레임 격자: `FramesX`열(yaw, 가로) × `FramesY`행(pitch, 세로). 예: 8×8.

---

## 1. 데이터 배치 (아틀라스 = "시트")

한 장의 텍스처에 `FramesX × FramesY` 타일을 격자로 채운다.

- 타일 `(c, r)`: **c = yaw 인덱스 → 아틀라스 X**, **r = pitch 인덱스 → 아틀라스 Y**.
- UV 원점 **좌하단**(Unity). `r=0` = 맨 아래 행, `r=FramesY-1` = 맨 위 행.

### 채널 패킹 (Amplify 규약)

| 텍스처 | RGB | A |
|---|---|---|
| `_Albedo` | 알베도 색 | **커버리지(실루엣 알파)** → `clip`에 사용 |
| `_Normals` | **오브젝트 공간 노말** `n*0.5+0.5` | **깊이** (0.5 = 표면 중앙 평면) |
| `_Specular` (옵션) | 스페큘러 색 | 스무스니스 |
| `_Position` (옵션) | 오브젝트 위치 `(p-min)/size` | >0이면 위치맵 사용 |

> 노말은 **오브젝트 공간**이다(탄젠트 공간 아님). 런타임에 `ObjectToWorld`로 변환한다.
> 깊이는 정규화값이며 표면 중앙이 0.5. 패럴랙스와 깊이출력에 쓰인다.

---

## 2. 베이킹 명세 (아무 렌더러로도 가능)

각 타일 `(c, r)`을 **직교(orthographic) 카메라**로 렌더한다. 카메라는 피벗(바운드 중심)을 바라본다.

### 2.1 프레임별 카메라 방향 `dir(c, r)`

런타임 선택식의 역산으로 유도한 **셀 중심 방향**:

```
φ = 2π · c / FramesX                        // 방위각(yaw)
β = π · r / (FramesY - 1)                    // 아래극(0)~위극(π)

dir.y = -cos(β)                              // 세로 성분
h     =  sin(β)                              // 수평 성분 크기 (= sqrt(1 - dir.y²))
dir.x = -cos(φ) · h
dir.z = -sin(φ) · h
```

- `dir`은 피벗→카메라. 카메라 위치 = `pivot + dir · (충분히 큰 거리)`.
- 카메라는 `-dir`(피벗)을 바라봄. **up 벡터 = 월드 up (0,1,0)** 로 고정 (극에서만 예외처리).
- **중요:** up을 반드시 월드 up으로 둔다. 카메라 자체의 roll을 넣지 않는다.
  런타임의 빌보드도 월드 up 기준으로 만들어지므로, 이 일치가 roll 보정을 성립시킨다.

### 2.2 프레이밍 (직교 크기)

- 오브젝트 전체가 모든 각도에서 들어오도록, 모든 프레임의 화면 바운드 최댓값을 구해
  그 값을 `xyFitSize`로 삼는다. 직교 half-size = `xyFitSize / 2`.
- 각 프레임은 **같은 스케일**로 렌더한다(프레임마다 확대율 다르면 안 됨).

### 2.3 참고: Amplify가 실제로 쓰는 회전 행렬

```
pitch = Euler(-180/(FramesY-1) · r,  0, 0)
yaw   = Euler( 0,  360/FramesX · c,  0)
camRotation = pitch · yaw
```

위 2.1의 `dir(c,r)`과 동일한 카메라 배치를 만든다(좌표 규약에 맞춰 유도한 형태가 2.1).
직접 베이크할 때는 2.1의 `dir`을 look-at으로 쓰는 편이 명확하다.

---

## 3. 머티리얼 파라미터 (정확한 정의와 계산법)

| 파라미터 | 타입 | 정의 / 계산 |
|---|---|---|
| `_FramesX`, `_FramesY` | float | 격자 수 |
| `_ImpostorSize` | float | **빌보드 메시의 오브젝트공간 정점 범위**(= 프레임 UV 스케일 기준). 직교 fit size와 맞춤 |
| `_Offset` | float4 | `.xyz` = **원본 바운드 중심(로컬)**. 피벗↔중심 차이 보정. `.w` = 미사용(스페리컬) |
| `_AI_SizeOffset` | float4 | `.x`=fitSize, `.y`=depthSize, **`.zw`=실루엣 pixelOffset의 UV 기여분** |
| `_DepthSize` | float | 깊이출력 스케일(깊이 재구성용) |
| `_ClipMask` | float | 알파 컷 임계값(예 0.5) |
| `_TextureBias` | float | 텍스처 밉 바이어스(경계 번짐 억제, 예 -1) |
| `_Parallax` | float | 패럴랙스 강도(옵션) |

### 3.1 pixelOffset ↔ _AI_SizeOffset.zw

실루엣 중심(centroid)이 프레임 정중앙이 아닐 때의 보정:

```
pixelOffset = (silhouetteCentroid - 0.5) · xyFitSize        // 월드 단위
_AI_SizeOffset.z = (pixelOffset.x / xyFitSize) / FramesX
_AI_SizeOffset.w = (pixelOffset.y / xyFitSize) / FramesY
```

역으로 셰이더에서 복원(일반 Quad 사용 시):

```
pixelOffset = _AI_SizeOffset.zw · _ImpostorSize · float2(FramesX, FramesY)
```

대칭 오브젝트면 `_AI_SizeOffset.zw ≈ 0` 이라 무시해도 된다.

---

## 4. 메시 명세

- 볼록 폴리곤(기본 **팔각형**)을 삼각분할한 판때기. 오버드로우를 줄인다.
- 기본 팔각형 점(모서리 컷 0.15, [0,1] 공간): `(.15,0)(.85,0)(1,.15)(1,.85)(.85,1)(.15,1)(0,.85)(0,.15)`
- **정점 위치**: `vertex.xy = (point - 0.5) · _ImpostorSize`, `z = 0`. (중앙정렬. 피벗은 셰이더가 처리)
- 볼록이므로 **fan 삼각분할**(0,i,i+1)로 충분. Winding은 카메라를 향해 앞면이 되게(안 보이면 뒤집기).
- **메시 UV는 런타임에 쓰지 않는다.** 프레임 UV는 셰이더가 정점 위치에서 직접 계산한다.

---

## 5. 정점 셰이더 — 전 라인 유도

입력: 정점 `positionOS`(xy = uvExpansion). 출력: `frameUV`, 클립좌표, (옵션)패럴랙스 UV.

### 5.1 오브젝트 공간 카메라 방향

```hlsl
// 원근: 카메라 월드좌표를 오브젝트 공간으로. 직교: 카메라 방향축을 멀리 밀어 사용
float3 worldCameraPos = (UNITY_MATRIX_P[3][3]==1)     // 직교 판정
    ? objectOrigin + cameraForwardAxis * 5000
    : _WorldSpaceCameraPos;

float3 objectCameraPosition = mul(WorldToObject, float4(worldCameraPos,1)).xyz - _Offset.xyz;
float3 dir = normalize(objectCameraPosition);         // 피벗(=_Offset)기준 방향
```

- `- _Offset.xyz`: 방향의 원점을 트랜스폼 원점이 아니라 **바운드 중심**으로 옮김(피벗 보정 ①).

### 5.2 빌보드 기저 (회전이 일어나는 평면)

```hlsl
float3 up    = float3(0,1,0);
float3 hori  = normalize(cross(dir, up));   // 빌보드 오른쪽
float3 vertV = cross(hori, dir);            // 빌보드 위(월드up을 dir⊥평면에 투영)
```

`{hori, vertV, dir}`는 오른손 정규직교 기저. 쿼드는 `hori–vertV` 평면에 놓인다.

### 5.3 프레임 선택 — YAW(열)

```hlsl
float sizeX = FramesX;
float verticalAngle = frac(atan2(-dir.z, -dir.x) / (2π)) * sizeX + 0.5;
int   col = floor(verticalAngle);           // 선택 열
```

- `atan2(-dir.z,-dir.x)`: `-dir` 기준 방위각. `φ=0`이면 `dir=(-1,0,0)`(열 0 = 카메라 −X쪽).
- `frac(.../2π)`: 방위각을 `[0,1)`로 감쌈 → 360°=0° **seam 자동 연결**.
- `·sizeX`: `[0, FramesX)`로 스케일. `+0.5`: 정수 경계를 두 열 **중간**에 오게(=`frac=0.5`가 열 중심).

### 5.4 프레임 선택 — PITCH(행)

```hlsl
float sizeY = FramesY - 1;                   // 양 끝이 극을 공유 → 간격은 FramesY-1
float axisSizeFraction = 1.0 / sizeY;
float verticalDot = dot(dir, up);            // = dir.y = sin(고도각)
float upAngle = acos(-verticalDot) / π + axisSizeFraction * 0.5;
int   row = min(floor(upAngle * sizeY), sizeY);
```

- `acos(-dir.y)`: 아래극(dir.y=-1)→0, 위극(dir.y=1)→π. `/π`로 `[0,1]`.
- `+0.5/sizeY`: 반 행 바이어스(floor 반올림을 행 중심에 맞춤 = yaw의 `+0.5`와 같은 역할).
- `min(...,sizeY)`: 위극에서 범위 초과 방지.

### 5.5 ROLL 보정 (핵심)

```hlsl
float yRot = (1.0/FramesX) * π * verticalDot * (2*frac(verticalAngle) - 1);
```

**분해:**
```
δφ  = (2·frac(verticalAngle) - 1) · (π / FramesX)   // 선택 열 중심에서 벗어난 방위각 ∈ [-π/FramesX, +π/FramesX]
yRot = sin(고도각) · δφ                              // (극에 가까운 정도) × (벗어난 각)
```

**유도(왜 sin·δφ):** 카메라가 중심을 보며 up=월드up을 유지한 채 방위각을 `dφ` 돌리면,
시야는 시선축 기준으로 `sin(θ)·dφ` 만큼 회전한다(구면 위 평행이동/홀로노미).
적분하면 `∫sinθ dφ = sinθ·δφ`. 지구본에서 극으로 갈수록 경도선이 모이는 그 현상이다.
- 적도(θ=0): sin=0 → roll 0. 극(θ=±90°): sin=±1 → roll 최대.
- 열 중심(frac=0.5): δφ=0 → roll 0.

### 5.6 빌보드 정점 만들기 (회전 적용)

```hlsl
float2 uvExpansion = positionOS.xy;

// (선택) 일반 Quad면 pixelOffset 복원 → Amplify 메시와 동일한 정점
#if RECONSTRUCT_PIVOT
    uvExpansion += _AI_SizeOffset.zw * _ImpostorSize * float2(FramesX, FramesY);
#endif

float cy = cos(yRot), sy = sin(yRot);
float2 uvRotator = mul(uvExpansion, float2x2(cy,-sy, sy,cy));   // 행벡터×행렬
//  uvRotator.x =  uv.x·cy + uv.y·sy
//  uvRotator.y = -uv.x·sy + uv.y·cy
float3 billboard = hori * uvRotator.x + vertV * uvRotator.y;
```

### 5.7 프레임 UV 계산

```hlsl
float2 sizeFraction    = float2(1.0/FramesX, 1.0/FramesY);
float  fractionsUVscale= 1.0/_ImpostorSize;
float2 relativeCoords  = float2(col, row);
float2 uvOffset        = _AI_SizeOffset.zw;

// 주의: UV는 "회전 안 한" uvExpansion 으로 계산 (billboard만 회전)
float2 frameUV = ((uvExpansion*fractionsUVscale + 0.5) + relativeCoords) * sizeFraction - uvOffset;
```

- `uvExpansion*fractionsUVscale + 0.5`: 정점을 타일 내 `[0,1]`로. (그래서 `_ImpostorSize`=정점범위 규칙)
- `+relativeCoords) * sizeFraction`: 해당 타일 위치로 이동/축소.
- `- uvOffset`: 5.6에서 더한 pixelOffset의 UV 기여분을 **정확히 상쇄**(정점은 이동, 텍스처는 제자리).

### 5.8 위치·패럴랙스(옵션)·출력

```hlsl
float3 positionOS_final = billboard + _Offset.xyz;   // 피벗 보정 ①
clipPos = TransformObjectToHClip(positionOS_final);

#if USE_PARALLAX
    float3 objNormalVec = cross(hori, -vertV);
    float3x3 worldToLocal = float3x3(hori, vertV, objNormalVec);
    float3 sphereLocal = normalize(mul(worldToLocal, billboard - objectCameraPosition));
    parallaxUV = sphereLocal.xy * sizeFraction * _Parallax;   // frameUV.zw
#endif
```

---

## 6. 프래그먼트 셰이더 — 전 라인

```hlsl
// (옵션) 패럴랙스: 깊이로 UV를 시차 이동
#if USE_PARALLAX
    float d = tex2Dbias(_Normals, float4(frameUV.xy,0,-1)).a;   // 깊이(alpha)
    frameUV.xy = (0.5 - d) * parallaxUV + frameUV.xy;
#endif

// 알베도 + 알파 컷
float4 albedo = tex2Dbias(_Albedo, float4(frameUV.xy,0,_TextureBias));
clip(albedo.a - _ClipMask);

// 노말: 오브젝트공간 → 월드
float4 nSample = tex2Dbias(_Normals, float4(frameUV.xy,0,_TextureBias));
float3 objNormal = nSample.xyz*2 - 1;
float3 worldNormal = normalize(mul((float3x3)ObjectToWorld, objNormal));

// (옵션) 깊이 출력: 임포스터가 3D처럼 교차/그림자 받게
#if WRITE_DEPTH
    float objectScale = length(ObjectToWorld[2].xyz);
    float depth = (nSample.a - 0.5) * _DepthSize * objectScale;
    float3 viewDir = (UNITY_MATRIX_P[3][3]==1) ? float3(0,0,1) : normalize(-viewPos.xyz);
    viewPos.xyz += viewDir * (depth + _AI_ForwardBias*objectScale);
    clipPos = mul(UNITY_MATRIX_P, float4(viewPos.xyz,1));
    // SV_Depth 로 clipPos.z/clipPos.w 출력
#endif

// 라이팅 (여기서는 메인라이트 N·L + 앰비언트 SH 예시)
```

---

## 7. 피벗 보정 정리 (두 부분)

| 오프셋 | 정체 | 적용 위치 |
|---|---|---|
| `_Offset.xyz` (바운드 중심) | 트랜스폼 원점 ↔ 시각 중심 차이 | **셰이더**: `billboard + _Offset`, 방향 원점도 `- _Offset` |
| `pixelOffset` (`_AI_SizeOffset.zw`) | 실루엣 서브프레임 미세보정 | Amplify는 **메시 정점**, 여기선 셰이더 `RECONSTRUCT_PIVOT` |

`_Offset.xyz` 계산 = 원본 메시들의 **로컬 바운드 중심**(자식 렌더러 합산).

---

## 8. 엣지 케이스

- **극(pole)**: `dir ∥ up`이면 `cross(dir,up)=0` → `hori` 불안정. 부동소수로 거의 안 걸리고,
  걸려도 대칭이라 roll 방향 무의미. (Hemi 변형은 `dir.y = max(0.001, dir.y)`로 아래극 제거)
- **Seam(360°=0°)**: `frac`이 자동 연결. `col`이 `FramesX-1 → 0`으로 감싸도 연속.
- **경계 번짐**: 타일 사이 bilinear 누수. `_TextureBias` 음수 + 베이크시 실루엣 dilation으로 완화.
- **회전 부호**: 축 손잡이/UV Y반전에 따라 roll이 반대로 보일 수 있음 → `yRot *= -1`로 뒤집기.

---

## 9. 검증 체크리스트

1. 카메라를 오브젝트 주위로 **수평 궤도** → 프레임이 매끄럽게 전환(seam에서 안 끊김).
2. 카메라를 **위/아래로** → 극에 가까울수록 스프라이트가 굴러(roll) 이음매 없이 정렬.
3. `_Offset` 세팅 후 임포스터가 원본 **위치에 정확히** 앉는지.
4. 노말 라이팅이 조명 방향에 자연스럽게 반응(안쪽이 밝으면 노말 sRGB/부호 문제).
5. (깊이출력 시) 임포스터가 지면과 자연스럽게 교차/그림자 수신.

---

## 10. 참고 구현 파일

- 셰이더: [`SphericalImpostor.shader`](SphericalImpostor.shader) — 5·6장의 실제 HLSL
- 메시/피벗: [`Editor/ImpostorMeshGenerator.cs`](Editor/ImpostorMeshGenerator.cs) — 4·7장
- 쉬운 설명: [`ROLL_EXPLAINED.md`](ROLL_EXPLAINED.md) — 5.5장의 고교 수준 버전

> 원본: Amplify Impostors (Amplify Creations)의 Spherical 임포스터 수식 참고.
