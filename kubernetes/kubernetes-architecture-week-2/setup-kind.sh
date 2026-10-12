#!/usr/bin/env bash
# kind ile 1 control-plane + 3 worker Kubernetes cluster kurar ve doğrular.
# Kullanım: ./setup-kind.sh            (kur + doğrula)
#           ./setup-kind.sh --delete   (cluster'ı sil)
set -euo pipefail

CLUSTER_NAME="${CLUSTER_NAME:-egitim}"
KIND_VERSION="${KIND_VERSION:-v0.33.0}"
WORKERS="${WORKERS:-3}"

log()  { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
ok()   { printf '\033[1;32m✔ %s\033[0m\n' "$*"; }
fail() { printf '\033[1;31m✘ %s\033[0m\n' "$*" >&2; exit 1; }

if [[ "${1:-}" == "--delete" ]]; then
  kind delete cluster --name "$CLUSTER_NAME"
  exit 0
fi

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
case "$(uname -m)" in
  x86_64|amd64)  ARCH=amd64 ;;
  aarch64|arm64) ARCH=arm64 ;;
  *) fail "Desteklenmeyen mimari: $(uname -m)" ;;
esac
log "Platform: $OS/$ARCH"

install_bin() { # $1=url $2=isim
  local tmp; tmp="$(mktemp)"
  curl -fsSL -o "$tmp" "$1"
  sudo install -m 0755 "$tmp" "/usr/local/bin/$2"
  rm -f "$tmp"
}

# 1) Docker
if [[ "$OS" == "linux" ]]; then
  if ! command -v docker >/dev/null; then
    log "Docker kuruluyor"
    curl -fsSL https://get.docker.com | sudo sh
  fi
  sudo systemctl enable --now docker >/dev/null 2>&1 || true
  if ! id -nG "$USER" | grep -qw docker; then
    log "$USER docker grubuna ekleniyor"
    sudo usermod -aG docker "$USER"
  fi
  # Grup bu oturumda henüz aktif değilse script'i docker grubuyla yeniden çalıştır.
  if ! docker info >/dev/null 2>&1; then
    if [[ -z "${KIND_SETUP_REEXEC:-}" ]]; then
      log "docker grubu bu oturumda aktif değil, script docker grubuyla yeniden başlatılıyor"
      exec sg docker -c "KIND_SETUP_REEXEC=1 CLUSTER_NAME=$CLUSTER_NAME KIND_VERSION=$KIND_VERSION WORKERS=$WORKERS bash '$(realpath "$0")'"
    fi
    fail "Docker'a erişilemiyor. Oturumu kapatıp açın ve script'i tekrar çalıştırın."
  fi
elif [[ "$OS" == "darwin" ]]; then
  docker info >/dev/null 2>&1 || fail "Docker çalışmıyor. Docker Desktop'ı açıp tekrar deneyin."
else
  fail "Desteklenmeyen işletim sistemi: $OS"
fi
ok "Docker hazır ($(docker version --format '{{.Server.Version}}'))"

# 2) inotify limitleri (yalnızca Linux; çok node'lu kind için gerekli)
if [[ "$OS" == "linux" ]]; then
  log "inotify limitleri ayarlanıyor"
  printf 'fs.inotify.max_user_watches=524288\nfs.inotify.max_user_instances=512\n' \
    | sudo tee /etc/sysctl.d/99-kind.conf >/dev/null
  sudo sysctl -q --system
  ok "inotify: watches=$(sysctl -n fs.inotify.max_user_watches) instances=$(sysctl -n fs.inotify.max_user_instances)"
fi

# 3) kubectl ve kind
if ! command -v kubectl >/dev/null; then
  log "kubectl kuruluyor"
  if [[ "$OS" == "darwin" ]]; then brew install kubectl; else
    install_bin "https://dl.k8s.io/release/$(curl -fsSL https://dl.k8s.io/release/stable.txt)/bin/linux/$ARCH/kubectl" kubectl
  fi
fi
ok "kubectl $(kubectl version --client -o json | grep -m1 gitVersion | cut -d'"' -f4)"

if ! command -v kind >/dev/null; then
  log "kind $KIND_VERSION kuruluyor"
  if [[ "$OS" == "darwin" ]]; then brew install kind; else
    install_bin "https://kind.sigs.k8s.io/dl/$KIND_VERSION/kind-linux-$ARCH" kind
  fi
fi
ok "$(kind version)"

# 4) Cluster
if kind get clusters 2>/dev/null | grep -qx "$CLUSTER_NAME"; then
  log "'$CLUSTER_NAME' cluster'ı zaten var, oluşturma atlanıyor"
else
  log "'$CLUSTER_NAME' cluster'ı oluşturuluyor (1 control-plane + $WORKERS worker)"
  CONFIG="$(mktemp)"
  {
    echo "kind: Cluster"
    echo "apiVersion: kind.x-k8s.io/v1alpha4"
    echo "nodes:"
    echo "  - role: control-plane"
    for _ in $(seq "$WORKERS"); do echo "  - role: worker"; done
  } > "$CONFIG"
  kind create cluster --name "$CLUSTER_NAME" --config "$CONFIG"
  rm -f "$CONFIG"
fi
kubectl config use-context "kind-$CLUSTER_NAME" >/dev/null

# 5) Doğrulama
log "Doğrulama"
kubectl wait --for=condition=Ready nodes --all --timeout=300s >/dev/null
EXPECTED=$((WORKERS + 1))
COUNT="$(kubectl get nodes --no-headers | wc -l | tr -d ' ')"
[[ "$COUNT" == "$EXPECTED" ]] || fail "Node sayısı $COUNT, beklenen $EXPECTED"
ok "$COUNT node Ready"

kubectl wait -n kube-system --for=condition=Ready pods --all --timeout=300s >/dev/null
ok "kube-system pod'ları Ready"

kubectl create deployment kind-smoke --image=nginx --replicas=6 >/dev/null
trap 'kubectl delete deployment kind-smoke --ignore-not-found >/dev/null 2>&1 || true' EXIT
kubectl rollout status deployment/kind-smoke --timeout=300s >/dev/null
SPREAD="$(kubectl get pods -l app=kind-smoke -o jsonpath='{range .items[*]}{.spec.nodeName}{"\n"}{end}' | sort -u | wc -l | tr -d ' ')"
[[ "$SPREAD" -ge 2 ]] || fail "Test pod'ları yalnızca $SPREAD node'a dağıldı"
ok "6 test pod'u $SPREAD worker'a dağıldı"

echo
kubectl get nodes -o wide
printf '\n\033[1;32mKurulum başarılı. Cluster: %s  (silmek için: %s --delete)\033[0m\n' "$CLUSTER_NAME" "$0"
