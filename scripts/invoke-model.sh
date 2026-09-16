#!/usr/bin/env bash
# scripts/invoke-model.sh
# Single dispatch point for all AI model invocations. Reads AI_MODEL from
# the environment (defaults to "codex") and
# delegates to the right provider CLI or API.
#
# Supported AI_MODEL values:
#   openai          OpenAI API (ChatGPT models via curl + API key)
#   codex           OpenAI Codex CLI (ChatGPT subscription auth)
#   deepseek        DeepSeek API (OpenAI-compatible endpoint)
#   moonshot        Moonshot/Kimi API (OpenAI-compatible endpoint)
#   openai-compat   Generic OpenAI-compatible endpoint (bring your own base URL)
#   openai-responses  OpenAI-compatible Responses API (bring your own base URL)
#
# Reads prompt from stdin, writes review to stdout, errors to stderr.
# Exit code reflects the underlying command's exit code.
set -euo pipefail

AI_MODEL="${AI_MODEL:-codex}"

# --- Provider: openai (OpenAI API, API-key auth) ---
invoke_openai() {
  # Reads OPENAI_API_KEY and OPENAI_MODEL from env.
  : "${OPENAI_API_KEY:?OPENAI_API_KEY is required for the openai provider}"
  local model="${OPENAI_MODEL:-gpt-4o}"
  local prompt
  prompt=$(cat)

  # Build JSON payload with jq for safe escaping
  local payload
  payload=$(jq -n \
    --arg model "$model" \
    --arg content "$prompt" \
    '{
      model: $model,
      messages: [{role: "user", content: $content}],
      temperature: 0.3,
      max_tokens: 4096
    }')

  local response
  response=$(curl -sf -X POST "https://api.openai.com/v1/chat/completions" \
    -H "Authorization: Bearer $OPENAI_API_KEY" \
    -H "Content-Type: application/json" \
    -d "$payload") || {
    echo "OpenAI API request failed" >&2
    return 1
  }

  # Extract content from response
  echo "$response" | jq -r '.choices[0].message.content // empty'
}

# --- Provider: codex (OpenAI Codex CLI, subscription auth) ---
invoke_codex() {
  # Codex CLI uses `codex exec` or `codex chat` for one-shot prompts.
  # Requires the Codex CLI installed and authenticated with `codex login`.
  codex exec --ephemeral --sandbox read-only -
}

# --- Provider: deepseek (DeepSeek API, OpenAI-compatible) ---
invoke_deepseek() {
  # Reads DEEPSEEK_API_KEY from env.
  : "${DEEPSEEK_API_KEY:?DEEPSEEK_API_KEY is required for the deepseek provider}"
  local model="${DEEPSEEK_MODEL:-deepseek-chat}"
  local prompt
  prompt=$(cat)

  local payload
  payload=$(jq -n \
    --arg model "$model" \
    --arg content "$prompt" \
    '{
      model: $model,
      messages: [{role: "user", content: $content}],
      temperature: 0.3,
      max_tokens: 4096
    }')

  curl -sf -X POST "https://api.deepseek.com/chat/completions" \
    -H "Authorization: Bearer $DEEPSEEK_API_KEY" \
    -H "Content-Type: application/json" \
    -d "$payload" | jq -r '.choices[0].message.content // empty'
}

# --- Provider: moonshot (Moonshot/Kimi API, OpenAI-compatible) ---
invoke_moonshot() {
  # Reads MOONSHOT_API_KEY from env.
  : "${MOONSHOT_API_KEY:?MOONSHOT_API_KEY is required for the moonshot provider}"
  local model="${MOONSHOT_MODEL:-moonshot-v1-8k}"
  local prompt
  prompt=$(cat)

  local payload
  payload=$(jq -n \
    --arg model "$model" \
    --arg content "$prompt" \
    '{
      model: $model,
      messages: [{role: "user", content: $content}],
      temperature: 0.3,
      max_tokens: 4096
    }')

  curl -sf -X POST "https://api.moonshot.cn/v1/chat/completions" \
    -H "Authorization: Bearer $MOONSHOT_API_KEY" \
    -H "Content-Type: application/json" \
    -d "$payload" | jq -r '.choices[0].message.content // empty'
}

# --- Provider: openai-compat (generic OpenAI-compatible endpoint) ---
invoke_openai_compat() {
  # Reads OPENAI_COMPAT_BASE_URL, OPENAI_COMPAT_API_KEY, and
  # OPENAI_COMPAT_MODEL from env. Works with any OpenAI-compatible API
  # (Ollama, vLLM, LiteLLM, local models, Azure OpenAI, etc.).
  : "${OPENAI_COMPAT_BASE_URL:?OPENAI_COMPAT_BASE_URL is required for the openai-compat provider}"
  : "${OPENAI_COMPAT_API_KEY:?OPENAI_COMPAT_API_KEY is required for the openai-compat provider}"
  local model="${OPENAI_COMPAT_MODEL:-gpt-4o}"
  local prompt
  prompt=$(cat)

  local payload
  payload=$(jq -n \
    --arg model "$model" \
    --arg content "$prompt" \
    '{
      model: $model,
      messages: [{role: "user", content: $content}],
      temperature: 0.3,
      max_tokens: 4096
    }')

  curl -sf -X POST "${OPENAI_COMPAT_BASE_URL%/}/chat/completions" \
    -H "Authorization: Bearer $OPENAI_COMPAT_API_KEY" \
    -H "Content-Type: application/json" \
    -d "$payload" | jq -r '.choices[0].message.content // empty'
}

# --- Provider: openai-responses (OpenAI-compatible Responses API) ---
invoke_openai_responses() {
  : "${OPENAI_RESPONSES_BASE_URL:?OPENAI_RESPONSES_BASE_URL is required for the openai-responses provider}"
  : "${OPENAI_RESPONSES_API_KEY:?OPENAI_RESPONSES_API_KEY is required for the openai-responses provider}"
  local model="${OPENAI_RESPONSES_MODEL:-gpt-5.6-sol}"
  local auth_mode="${OPENAI_RESPONSES_AUTH_MODE:-bearer}"
  local auth_header
  case "$auth_mode" in
    bearer)
      auth_header="Authorization: Bearer ${OPENAI_RESPONSES_API_KEY}"
      ;;
    api-key)
      auth_header="api-key: ${OPENAI_RESPONSES_API_KEY}"
      ;;
    *)
      echo "Unsupported OPENAI_RESPONSES_AUTH_MODE: ${auth_mode}" >&2
      return 1
      ;;
  esac
  local prompt
  prompt=$(cat)

  local payload
  payload=$(jq -n \
    --arg model "$model" \
    --arg input "$prompt" \
    '{
      model: $model,
      input: $input,
      max_output_tokens: 4096
    }')

  local response_file
  response_file=$(mktemp)
  local http_status
  local curl_status=0
  http_status=$(curl --silent -X POST "${OPENAI_RESPONSES_BASE_URL%/}/responses" \
    -H "$auth_header" \
    -H "Content-Type: application/json" \
    -d "$payload" \
    --output "$response_file" \
    --write-out '%{http_code}') || curl_status=$?

  if ((curl_status != 0)); then
    rm -f "$response_file"
    echo "Responses API request failed during transport." >&2
    return 1
  fi

  if [[ ! "$http_status" =~ ^2[0-9][0-9]$ ]]; then
    rm -f "$response_file"
    echo "Responses API request failed with HTTP status ${http_status}." >&2
    return 1
  fi

  local output
  if ! output=$(jq -er '
    (
      .output_text? //
      ([.output[]?.content[]? | select(.type? == "output_text") | .text?]
        | map(select(type == "string"))
        | join("\n"))
    )
    | select(type == "string" and length > 0)
  ' "$response_file"); then
    rm -f "$response_file"
    echo "Responses API returned no text output." >&2
    return 1
  fi

  rm -f "$response_file"
  printf '%s\n' "$output"
}

# --- Dispatch ---
case "$AI_MODEL" in
  openai)
    invoke_openai
    ;;
  codex)
    invoke_codex
    ;;
  deepseek)
    invoke_deepseek
    ;;
  moonshot)
    invoke_moonshot
    ;;
  openai-compat)
    invoke_openai_compat
    ;;
  openai-responses)
    invoke_openai_responses
    ;;
  *)
    echo "Unknown AI_MODEL: $AI_MODEL. Supported: openai, codex, deepseek, moonshot, openai-compat, openai-responses" >&2
    exit 1
    ;;
esac
