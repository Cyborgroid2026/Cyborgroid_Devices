# Cyborgroid AI — 오프라인 로컬 Personal AI 에이전트 (터미널)

`cyborgroid` 를 입력하면 네트워크/와이파이가 꺼져 있어도 동작하는 개인 AI 에이전트입니다.
대화만 하는 것이 아니라, 실행한 폴더에서 파일을 읽고·검색하고·수정하고 명령을 실행할 수 있습니다.
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

대화 중 명령: `/think` 깊은 추론, `/showthink` 추론 과정 보기, `/tools [on|off]` 에이전트 도구 전환,
`/clear`, `/sessions`, `/load <id>`, `/status`, `/exit`.
답변 중 `Ctrl+C` 를 누르면 생성을 멈춥니다.

## 에이전트 도구

모델은 필요할 때 아래 도구를 스스로 호출하고, 결과를 보고 다음 행동을 정합니다(요청당 최대 `assistant.max_steps` 단계).
작업 폴더는 `cyborgroid` 를 실행한 현재 폴더입니다.

| 도구 | 하는 일 | 확인 |
|---|---|---|
| `read_file` | 파일 읽기 (줄 번호 포함) | 묻지 않음 |
| `list_dir` | 폴더 목록 | 묻지 않음 |
| `search` | 정규식으로 파일 내용 검색 | 묻지 않음 |
| `write_file` | 파일 생성·덮어쓰기 | **매번 묻기** |
| `edit_file` | 파일의 한 부분 교체 | **매번 묻기** |
| `run_shell` | bash 명령 실행 (기본 제한 120초) | **매번 묻기** |

확인 질문에서 `y` 는 이번 한 번, `a` 는 이 대화 동안 같은 도구를 계속 허용, `n` (또는 Enter) 은 거부입니다.
명령 실행 중 `Ctrl+C` 를 누르면 명령을 멈춥니다. root(sudo)로 실행하면 수정·명령 도구는 거부됩니다.
대화만 하려면 `/tools off` 또는 설정에서 `assistant.tools: false`.

> 도구 사용에는 컨텍스트가 많이 듭니다. 기존 설치의 `engine.context` 가 8192 라면 16384 이상으로 올리는 것을 권장합니다
> (Qwen3-8B는 32768까지 지원, VRAM 사용량이 늘어납니다).

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
`assistant.system_prompt`, `assistant.think`, `assistant.tools`(에이전트 도구, 기본 true), `assistant.max_steps`,
`catalog`(다운로드할 수 있는 모델 목록).
테스트용으로 다른 설정 파일을 쓰려면 `CYBORGROID_CONFIG=/path/config.json` 을 지정합니다.

## 개발

```bash
cargo test --release --manifest-path core/Cargo.toml
dotnet build cli -c Release
```
