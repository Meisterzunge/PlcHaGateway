#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/PlcHaGateway.csproj"
DEFAULT_DEPLOY_DIR="/opt/plchagateway"
DEFAULT_CONFIGURATION="Release"
DEFAULT_SERVICE_FILE="/etc/systemd/system/PlcHaGateway.service"
DEFAULT_UNIT_TEMPLATE="$SCRIPT_DIR/PlcHaGateway.service"
DEFAULT_APPSETTINGS="$SCRIPT_DIR/appsettings.Production.json"
DEFAULT_HA_HOST="homeassistant.local"
DEFAULT_HA_PORT="8123"
DEFAULT_HA_SSL="false"
DEFAULT_HA_TOKEN=""
DEFAULT_MQTT_HOST="127.0.0.1"
DEFAULT_MQTT_USERNAME=""
DEFAULT_MQTT_PASSWORD=""
DEFAULT_PLC_NETID="5.30.166.30.1.1"
DEFAULT_PLC_PORT="851"
DEFAULT_CYCLIC_UPDATE="5"

DEPLOY_DIR=""
CONFIGURATION=""
SERVICE_USER=""
SERVICE_FILE=""
APPSETTINGS_SOURCE=""
FORCE_SERVICE_UNIT=0
NON_INTERACTIVE=0
ENABLE_SERVICE=""
START_SERVICE=""
HA_HOST=""
HA_PORT=""
HA_SSL=""
HA_TOKEN=""
MQTT_HOST=""
MQTT_USERNAME=""
MQTT_PASSWORD=""
PLC_NETID=""
PLC_PORT=""
CYCLIC_UPDATE=""

print_help() {
    cat <<'EOF'
Usage: ./deploy.sh [options]

Builds and deploys PlcHaGateway to a local Linux path and optionally installs/updates
PlcHaGateway.service.

Options:
  --deploy-dir <path>          Target deployment directory (default: /opt/plchagateway)
  --configuration <value>      dotnet publish configuration (default: Release)
  --service-user <name>        Dedicated Linux user for the service (default: plchagateway)
  --service-file <path>        Systemd unit destination (default: /etc/systemd/system/PlcHaGateway.service)
  --appsettings <path>         Appsettings source copied as appsettings.Production.json
                               (default: ./appsettings.Production.json)
  --force-service-unit         Overwrite local unit file without prompt
  --non-interactive            Use defaults for prompts (overwrite remains default=no)
  --enable-service <yes|no>    Enable service after deploy (default: yes)
  --start-service <yes|no>     Start/restart service after deploy (default: yes)
  -h, --help                   Show this help
EOF
}

require_command() {
    local cmd="$1"
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo "ERROR: Required command not found: $cmd" >&2
        exit 1
    fi
}

run_as_root() {
    if [ "${EUID}" -eq 0 ]; then
        "$@"
    else
        require_command sudo
        sudo "$@"
    fi
}

detect_default_service_user() {
    if [ -n "${SUDO_USER:-}" ]; then
        printf '%s' "$SUDO_USER"
    else
        id -un
    fi
}

prompt_with_default() {
    local prompt="$1"
    local default_value="$2"
    local answer

    if [ "$NON_INTERACTIVE" -eq 1 ]; then
        printf '%s' "$default_value"
        return
    fi

    read -r -p "$prompt [$default_value]: " answer
    if [ -z "$answer" ]; then
        printf '%s' "$default_value"
    else
        printf '%s' "$answer"
    fi
}

prompt_with_display_default() {
    local prompt="$1"
    local default_value="$2"
    local display_default="$3"
    local answer

    if [ "$NON_INTERACTIVE" -eq 1 ]; then
        printf '%s' "$default_value"
        return
    fi

    read -r -p "$prompt [$display_default]: " answer
    if [ -z "$answer" ]; then
        printf '%s' "$default_value"
    else
        printf '%s' "$answer"
    fi
}

normalize_yes_no() {
    local value="$1"
    case "${value,,}" in
        y|yes|true|1)
            printf 'yes'
            ;;
        n|no|false|0)
            printf 'no'
            ;;
        *)
            echo "ERROR: Invalid yes/no value: $value" >&2
            exit 1
            ;;
    esac
}

normalize_boolean() {
    local value="$1"
    case "${value,,}" in
        y|yes|true|1)
            printf 'true'
            ;;
        n|no|false|0)
            printf 'false'
            ;;
        *)
            echo "ERROR: Invalid boolean value: $value" >&2
            exit 1
            ;;
    esac
}

ask_yes_no() {
    local prompt="$1"
    local default_value="$2"
    local response

    if [ "$NON_INTERACTIVE" -eq 1 ]; then
        response="$default_value"
    else
        local marker="y/N"
        if [ "$default_value" = "yes" ]; then
            marker="Y/n"
        fi

        read -r -p "$prompt [$marker]: " response
        if [ -z "$response" ]; then
            response="$default_value"
        fi
    fi

    response="$(normalize_yes_no "$response")"
    if [ "$response" = "yes" ]; then
        return 0
    fi

    return 1
}

read_json_value() {
    local file_path="$1"
    local section="$2"
    local key="$3"
    local scope
    local value

    if [ ! -f "$file_path" ]; then
        return 0
    fi

    if [ -n "$section" ]; then
        scope="$(sed -n "/^[[:space:]]*\"$section\"[[:space:]]*:[[:space:]]*{/,/^[[:space:]]*}[[:space:]]*,\{0,1\}[[:space:]]*$/p" "$file_path")"
    else
        scope="$(cat "$file_path")"
    fi

    value="$(printf '%s\n' "$scope" | sed -n "s/^[[:space:]]*\"$key\"[[:space:]]*:[[:space:]]*\"\(.*\)\"[[:space:]]*,\{0,1\}[[:space:]]*$/\1/p" | head -n 1)"
    if [ -n "$value" ]; then
        printf '%s' "$value"
        return 0
    fi

    value="$(printf '%s\n' "$scope" | sed -n "s/^[[:space:]]*\"$key\"[[:space:]]*:[[:space:]]*\([^,}][^,}]*\)[[:space:]]*,\{0,1\}[[:space:]]*$/\1/p" | head -n 1 | tr -d '[:space:]')"
    if [ -n "$value" ]; then
        printf '%s' "$value"
    fi
}

read_default_value() {
    local candidate="$1"
    local fallback="$2"

    if [ -n "$candidate" ]; then
        printf '%s' "$candidate"
    else
        printf '%s' "$fallback"
    fi
}

read_secret_prompt_default() {
    local candidate="$1"
    local fallback="$2"

    if [ -n "$candidate" ]; then
        printf '%s' '***'
    else
        printf '%s' "$fallback"
    fi
}

write_appsettings_file() {
    local file_path="$1"

    cat > "$file_path" <<EOF
{
    "HomeAssistant": {
        "Host": "$HA_HOST",
        "Port": $HA_PORT,
        "Ssl": $HA_SSL,
        "Token": "$HA_TOKEN"
    },
    "Mqtt": {
        "Host": "$MQTT_HOST",
        "UserName": "$MQTT_USERNAME",
        "Password": "$MQTT_PASSWORD"
    },
    "Plc": {
        "NetId": "$PLC_NETID",
        "Port": $PLC_PORT
    },
    "CyclicUpdate": $CYCLIC_UPDATE
}
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --deploy-dir)
            DEPLOY_DIR="$2"
            shift 2
            ;;
        --configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        --service-user)
            SERVICE_USER="$2"
            shift 2
            ;;
        --service-file)
            SERVICE_FILE="$2"
            shift 2
            ;;
        --appsettings)
            APPSETTINGS_SOURCE="$2"
            shift 2
            ;;
        --force-service-unit)
            FORCE_SERVICE_UNIT=1
            shift 1
            ;;
        --non-interactive)
            NON_INTERACTIVE=1
            shift 1
            ;;
        --enable-service)
            ENABLE_SERVICE="$(normalize_yes_no "$2")"
            shift 2
            ;;
        --start-service)
            START_SERVICE="$(normalize_yes_no "$2")"
            shift 2
            ;;
        -h|--help)
            print_help
            exit 0
            ;;
        *)
            echo "ERROR: Unknown argument: $1" >&2
            print_help
            exit 1
            ;;
    esac

done

require_command dotnet

DEFAULT_SERVICE_USER="$(detect_default_service_user)"

if [ ! -f "$PROJECT_FILE" ]; then
    echo "ERROR: Project file not found: $PROJECT_FILE" >&2
    exit 1
fi

DEPLOY_DIR="${DEPLOY_DIR:-$(prompt_with_default "Deploy directory" "$DEFAULT_DEPLOY_DIR")}"
CONFIGURATION="${CONFIGURATION:-$(prompt_with_default "Build configuration" "$DEFAULT_CONFIGURATION")}"
SERVICE_USER="${SERVICE_USER:-$(prompt_with_default "Dedicated service user" "$DEFAULT_SERVICE_USER")}"
SERVICE_FILE="${SERVICE_FILE:-$(prompt_with_default "Systemd unit target path" "$DEFAULT_SERVICE_FILE")}"
APPSETTINGS_SOURCE="${APPSETTINGS_SOURCE:-$DEFAULT_APPSETTINGS}"
ENABLE_SERVICE="${ENABLE_SERVICE:-$(normalize_yes_no "yes")}"
START_SERVICE="${START_SERVICE:-$(normalize_yes_no "yes")}"
SERVICE_UNIT_NAME="$(basename "$SERVICE_FILE")"

LAST_HA_HOST="$(read_json_value "$DEFAULT_APPSETTINGS" "HomeAssistant" "Host")"
LAST_HA_PORT="$(read_json_value "$DEFAULT_APPSETTINGS" "HomeAssistant" "Port")"
LAST_HA_SSL="$(read_json_value "$DEFAULT_APPSETTINGS" "HomeAssistant" "Ssl")"
LAST_HA_TOKEN="$(read_json_value "$DEFAULT_APPSETTINGS" "HomeAssistant" "Token")"
LAST_MQTT_HOST="$(read_json_value "$DEFAULT_APPSETTINGS" "Mqtt" "Host")"
LAST_MQTT_USERNAME="$(read_json_value "$DEFAULT_APPSETTINGS" "Mqtt" "UserName")"
LAST_MQTT_PASSWORD="$(read_json_value "$DEFAULT_APPSETTINGS" "Mqtt" "Password")"
LAST_PLC_NETID="$(read_json_value "$DEFAULT_APPSETTINGS" "Plc" "NetId")"
LAST_PLC_PORT="$(read_json_value "$DEFAULT_APPSETTINGS" "Plc" "Port")"
LAST_CYCLIC_UPDATE="$(read_json_value "$DEFAULT_APPSETTINGS" "" "CyclicUpdate")"

if [ "$APPSETTINGS_SOURCE" = "$DEFAULT_APPSETTINGS" ]; then
    HA_HOST="$(prompt_with_default "Home Assistant host" "$(read_default_value "$LAST_HA_HOST" "$DEFAULT_HA_HOST")")"
    HA_PORT="$(prompt_with_default "Home Assistant port" "$(read_default_value "$LAST_HA_PORT" "$DEFAULT_HA_PORT")")"
    HA_SSL="$(normalize_boolean "$(prompt_with_default "Home Assistant SSL" "$(read_default_value "$LAST_HA_SSL" "$DEFAULT_HA_SSL")")")"
    HA_TOKEN="$(prompt_with_display_default "Home Assistant token" "$(read_default_value "$LAST_HA_TOKEN" "$DEFAULT_HA_TOKEN")" "$(read_secret_prompt_default "$LAST_HA_TOKEN" "$DEFAULT_HA_TOKEN")")"
    MQTT_HOST="$(prompt_with_default "MQTT host" "$(read_default_value "$LAST_MQTT_HOST" "$DEFAULT_MQTT_HOST")")"
    MQTT_USERNAME="$(prompt_with_default "MQTT user name" "$(read_default_value "$LAST_MQTT_USERNAME" "$DEFAULT_MQTT_USERNAME")")"
    MQTT_PASSWORD="$(prompt_with_display_default "MQTT password" "$(read_default_value "$LAST_MQTT_PASSWORD" "$DEFAULT_MQTT_PASSWORD")" "$(read_secret_prompt_default "$LAST_MQTT_PASSWORD" "$DEFAULT_MQTT_PASSWORD")")"
    PLC_NETID="$(prompt_with_default "PLC NetId" "$(read_default_value "$LAST_PLC_NETID" "$DEFAULT_PLC_NETID")")"
    PLC_PORT="$(prompt_with_default "PLC ADS port" "$(read_default_value "$LAST_PLC_PORT" "$DEFAULT_PLC_PORT")")"
    CYCLIC_UPDATE="$(prompt_with_default "Cyclic update interval" "$(read_default_value "$LAST_CYCLIC_UPDATE" "$DEFAULT_CYCLIC_UPDATE")")"

    write_appsettings_file "$DEFAULT_APPSETTINGS"
fi

if [ ! -f "$DEFAULT_UNIT_TEMPLATE" ]; then
    echo "ERROR: Unit template not found in repo: $DEFAULT_UNIT_TEMPLATE" >&2
    exit 1
fi

if [ ! -f "$APPSETTINGS_SOURCE" ]; then
    echo "ERROR: Appsettings source not found: $APPSETTINGS_SOURCE" >&2
    exit 1
fi

PUBLISH_DIR="$(mktemp -d -t plchagateway-publish-XXXXXX)"
RENDERED_UNIT="$(mktemp -t plchagateway-service-XXXXXX)"
cleanup() {
    rm -rf "$PUBLISH_DIR"
    rm -f "$RENDERED_UNIT"
}
trap cleanup EXIT

echo "Publishing PlcHaGateway..."
dotnet publish "$PROJECT_FILE" -c "$CONFIGURATION" -o "$PUBLISH_DIR"

echo "Ensuring dedicated service user exists: $SERVICE_USER"
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    run_as_root useradd --system --user-group --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

echo "Deploying files to $DEPLOY_DIR"
run_as_root mkdir -p "$DEPLOY_DIR"
run_as_root cp -a "$PUBLISH_DIR"/. "$DEPLOY_DIR"/
run_as_root install -m 640 "$APPSETTINGS_SOURCE" "$DEPLOY_DIR/appsettings.Production.json"
run_as_root chown -R "$SERVICE_USER:$SERVICE_USER" "$DEPLOY_DIR"

sed -e "s|__DEPLOY_DIR__|$DEPLOY_DIR|g" \
    -e "s|__SERVICE_USER__|$SERVICE_USER|g" \
    "$DEFAULT_UNIT_TEMPLATE" > "$RENDERED_UNIT"

UNIT_UPDATED=0
if [ ! -f "$SERVICE_FILE" ]; then
    echo "No local unit found. Installing default unit: $SERVICE_FILE"
    run_as_root install -m 644 "$RENDERED_UNIT" "$SERVICE_FILE"
    UNIT_UPDATED=1
elif [ "$FORCE_SERVICE_UNIT" -eq 1 ]; then
    echo "Force enabled. Overwriting existing unit: $SERVICE_FILE"
    run_as_root install -m 644 "$RENDERED_UNIT" "$SERVICE_FILE"
    UNIT_UPDATED=1
else
    if ask_yes_no "Systemd unit already exists at $SERVICE_FILE. Overwrite?" "no"; then
        run_as_root install -m 644 "$RENDERED_UNIT" "$SERVICE_FILE"
        UNIT_UPDATED=1
    else
        echo "Keeping existing local unit file."
    fi
fi

if command -v systemctl >/dev/null 2>&1; then
    if [ "$UNIT_UPDATED" -eq 1 ]; then
        run_as_root systemctl daemon-reload
    fi

    if ask_yes_no "Enable PlcHaGateway service now?" "$ENABLE_SERVICE"; then
        run_as_root systemctl enable "$SERVICE_UNIT_NAME"
    fi

    if ask_yes_no "Start or restart PlcHaGateway service now?" "$START_SERVICE"; then
        if run_as_root systemctl is-active --quiet "$SERVICE_UNIT_NAME"; then
            run_as_root systemctl restart "$SERVICE_UNIT_NAME"
        else
            run_as_root systemctl start "$SERVICE_UNIT_NAME"
        fi
    fi
else
    echo "WARNING: systemctl was not found. Files were deployed, but service management was skipped."
fi

echo
echo "Deployment completed."
echo "- Deploy directory: $DEPLOY_DIR"
echo "- Runtime appsettings: $DEPLOY_DIR/appsettings.Production.json"
echo "- Service unit path: $SERVICE_FILE"
echo "- Service user: $SERVICE_USER"
