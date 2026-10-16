# Cyborgroid AI — 오프라인 로컬 Personal AI (터미널)

`cyborgroid` 를 입력하면 네트워크/와이파이가 꺼져 있어도 동작하는 개인 AI입니다.
네트워크는 **업데이트(`sudo cyborgroid update`)에만** 사용합니다.

> **모델에 대해:** Claude Opus 5.5 같은 Claude 모델은 가중치가 공개되지 않아
> Anthropic 서버에서만 동작하므로 오프라인으로 실행할 수 없습니다.
> Cyborgroid는 PC에 저장된 **오픈웨이트 LLM**(기본: Qwen3-8B, Apache-2.0)을 로컬에서 실행합니다.

## 구조

```
cyborgroid (C# / .NET 10)          터미널 UI, 대화·세션, 설정, 업데이트
   │  P/Invoke (C ABI)
libcyborgroid_core.so (Rust)       엔진 관리, 루프백 전용 HTTP 스트리밍,
   │  http://127.0.0.1:8765        SHA-256 검증, 네트워크 상태 확인
llama-server (llama.cpp, C++)      GGUF 모델 추론 (CPU / Vulkan / CUDA)
```

| 폴더 | 내용 |
|---|---|
| `core/` | Rust 코어. 외부 crate가 하나도 없어 오프라인으로 빌드됩니다. |
| `cli/` | C# CLI (`cyborgroid` 명령) |
| `scripts/build-engine.sh` | llama.cpp 엔진 빌드 (GPU 백엔드 자동 감지) |
| `install.sh` / `uninstall.sh` | 설치 / 제거 |

## 설치

필요한 도구: `cargo`, `dotnet` (10), `cmake`, `git`, C/C++ 컴파일러

```bash
sudo ./install.sh                # 빌드 + /opt/cyborgroid 에 설치
sudo cyborgroid update           # 기본 모델 다운로드 (~5GB, 네트워크 필요)
cyborgroid                       # 이제 와이파이를 꺼도 됩니다
```

## 사용법

```
cyborgroid                      대화 시작
cyborgroid ask "질문"           한 번만 묻기   (예: cat log.txt | cyborgroid ask "요약해줘")
cyborgroid status               엔진·모델·네트워크 상태
cyborgroid models               모델 목록
sudo cyborgroid use <모델>      기본 모델 변경
sudo cyborgroid update          모델 업데이트
     --model <이름> | --engine | --app | --all | --check
```

대화 중 명령: `/think` 깊은 추론, `/showthink` 추론 과정 보기, `/clear`, `/sessions`, `/load <id>`, `/status`, `/exit`.
답변 중 `Ctrl+C` 를 누르면 생성을 멈춥니다.

## sudo가 필요한 경우

설치, `update`, `use` 처럼 `/opt/cyborgroid` 와 `/etc/cyborgroid` 를 바꾸는 작업에만 필요합니다.
대화는 일반 사용자 권한으로 실행됩니다. AI를 root로 실행할 이유가 없고, 그렇게 하지 않는 편이 더 안전합니다.
대화 기록은 사용자별로 `~/.local/share/cyborgroid/sessions/` 에 저장됩니다.

## 네트워크·방화벽·국가 차단

- **대화:** 외부 네트워크를 전혀 쓰지 않습니다. 엔진은 `127.0.0.1` 에만 바인딩되어 LAN에도 노출되지 않고,
  Rust 코어가 루프백이 아닌 주소로의 연결을 거부합니다. 엔진은 다운로드 기능 없이(`LLAMA_CURL=OFF`) 빌드됩니다.
  따라서 방화벽이나 국가 차단의 영향을 받지 않습니다.
- **업데이트:** `huggingface.co` (모델)와 `github.com` (엔진)에 접속합니다. 회사 방화벽이나 일부 국가에서
  이 사이트가 차단되어 있으면 업데이트가 실패합니다. 이 경우:
  - `/etc/cyborgroid/config.json` 의 `catalog[].url` 또는 `update.manifest_url` 을 미러 주소로 바꾸거나,
  - 다른 PC에서 받은 `.gguf` 파일을 USB 등으로 `/opt/cyborgroid/models/` 에 복사하면 됩니다(파일명은 `cyborgroid models` 카탈로그와 같아야 합니다).
- 다운로드한 모델은 SHA-256으로 검증합니다(Hugging Face가 공개한 해시와 비교).

## 설정 (`/etc/cyborgroid/config.json`)

주요 항목: `model`, `engine.context`(컨텍스트 길이), `engine.gpu_layers`(GPU에 올릴 레이어 수, 0 = CPU),
`assistant.system_prompt`, `assistant.think`, `catalog`(다운로드할 수 있는 모델 목록).
테스트용으로 다른 설정 파일을 쓰려면 `CYBORGROID_CONFIG=/path/config.json` 을 지정합니다.

## 개발

```bash
cargo test --release --manifest-path core/Cargo.toml
dotnet build cli -c Release
```
