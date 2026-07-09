# Custom Spherical Impostor (URP)

Amplify Impostors의 **Spherical 임포스터 런타임 매커니즘**을 Unity URP에서 재현한 경량 구현입니다.
목표: **yaw / pitch로 프레임(이미지) 선택 + roll 완벽 재현**.

> 학습·재현 목적의 재구현입니다. 아틀라스(시트)는 Amplify Impostors로 굽거나 직접 준비해야 합니다.

## 구성

| 파일 | 설명 |
|---|---|
| `SphericalImpostor.shader` | URP Lit 임포스터 셰이더. yaw/pitch 프레임 선택, roll 보정(`yRot`), 오브젝트공간 노말 라이팅, 피벗 보정(`_Offset` + `_ReconstructPivot`) |
| `Editor/ImpostorMeshGenerator.cs` | 빌보드 메시(Quad/팔각형/N-Gon) 생성기 + 바운드 중심 자동 계산(`_Offset` 세팅) EditorWindow |

## 핵심 매커니즘

정점 셰이더에서 오브젝트공간 카메라 방향 `dir`로부터:

- **Yaw(열 선택)**: `verticalAngle = frac(atan2(-dir.z,-dir.x)/2π)·FramesX + 0.5`
- **Pitch(행 선택)**: `upAngle = acos(-dot(dir,up))/π + 0.5/(FramesY-1)`
- **Roll 재현**: `yRot = (1/FramesX)·π·dot(dir,up)·(2·frac(verticalAngle)-1) = sin(elevation)·δφ`
  빌보드 UV(정점 위치)를 `yRot`만큼 회전 → 이산 프레임 사이 이음매 제거

roll 보정량은 (극에 가까운 정도) × (베이크 열 중심에서 벗어난 방위각)에 비례합니다. 경도선이 극에서 수렴하며 생기는 회전을 상쇄합니다.

> 회전(roll) 공식을 쉽게 풀어쓴 설명: **[ROLL_EXPLAINED.md](ROLL_EXPLAINED.md)** (고등학교 수학 수준)
> 처음부터 완전 재구현용 상세 명세: **[IMPLEMENTATION.md](IMPLEMENTATION.md)** (베이킹 각도·채널 패킹·전 라인 유도)

## 사용법

### 1) 셰이더
1. Amplify Impostors로 **Spherical** 타입 아틀라스를 굽습니다(또는 직접 준비).
2. 머티리얼 셰이더를 `Custom/Spherical Impostor (Lit)`로 설정.
3. 프로퍼티 입력:
   - `_Albedo` / `_Normals`: 아틀라스 텍스처
   - `Frames X` / `Frames Y`: 격자 수 (예: 8 / 8)
   - `Impostor Size`: 메시의 오브젝트공간 정점 범위와 동일
   - `Pivot Offset (_Offset)`: 원본 바운드 중심(로컬) — 아래 생성기로 자동 계산 가능
   - `Reconstruct Pivot`: 일반 Quad 사용 시 ON

### 2) 메시 생성기
메뉴 `Tools > Custom Impostor > Billboard Mesh Generator`
- Shape/Size 선택 후 **Generate & Save** 또는 선택 오브젝트에 바로 할당
- **Pivot 자동 계산**: Source Object + Target Material 지정 → `Compute Bounds Center → Set _Offset`

## 제외된 것 (현재 범위)

- 패럴랙스 / 깊이 출력(SV_Depth) — 실루엣은 정확하나 깊이 기반 교차·자기그림자는 미지원
- Specular / Occlusion / Emission / Position 맵

## 크레딧

Amplify Impostors (Amplify Creations)의 Spherical 임포스터 수식을 참고한 재구현입니다.
