# Week 6: Sıfırdan Kurulum

Bu dosya, week-6'daki tüm kurulumu baştan sona tek seferde yapmak için çalıştırılacak komutları sırayla
verir:

1. Hetzner'de k3s cluster'ı (1 master + 2 worker)
2. Traefik ile Gateway API
3. GitHub Actions ile Week6.API'nin private GHCR'den deploy'u
4. `order.kiteloomstudio.com` alan adının uygulamaya yönlendirilmesi
5. Ders sonunda tüm kaynakların silinmesi

Her adımın ayrıntılı açıklaması ilgili rehberdedir:

- [hetzner-k3s/hetzner-k3s.md](hetzner-k3s/hetzner-k3s.md)
- [PrivateRegistry/private-registry.md](PrivateRegistry/private-registry.md)
- [GatewayAPI/gateway-api.md](GatewayAPI/gateway-api.md)

> Test edildi: 16.11.2026, hetzner-k3s v2.11.0, k3s v1.37.2+k3s1, hcloud v1.70.2, kubectl v1.36.1, gh v2.102.1.
> Toplam süre yaklaşık 10 dakika. Bunun 6 dakikası cluster kurulumudur.

## Sonuç

```text
Kullanıcı ── http://order.kiteloomstudio.com/
                │ DNS (Cloudflare, DNS only)
                ▼
        Hetzner LB "traefik" :80
                │ private network
                ▼
        Traefik pod (Gateway week6-gateway, HTTPRoute week6-api)
                │
                ▼
        Service week6-api (ClusterIP) ──► 3 pod (image: ghcr.io/fcakiroglu16/week6-api, private)
```

Hetzner'de oluşan kaynaklar:

| Kaynak | Sayı | Ne |
|---|---|---|
| server | 3 | `egitim-master1`, `egitim-pool-worker-worker1`, `egitim-pool-worker-worker2` (cx23, fsn1) |
| primary-ip | 8 | 3 sunucu ve 1 LB için birer IPv4 ve IPv6 |
| load-balancer | 1 | `traefik` (lb11). hetzner-k3s değil, CCM oluşturur. |
| network | 1 | `egitim`, 10.0.0.0/16 |
| firewall | 1 | `egitim` |
| placement-group | 2 | master ve worker için |
| ssh-key | 1 | `egitim` |

---

## 0. Ön koşullar

Tüm komutlar `kubernetes/week-6` klasöründen çalıştırılır:

```bash
cd ~/RiderProjects/docker-kubernetes-for-dotnet-developers-oct-2026/kubernetes/week-6
```

### 0.1 Araçlar

```bash
brew install vitobotta/tap/hetzner_k3s hcloud kubectl gh jq
```

```bash
hetzner-k3s --version
hcloud version
kubectl version --client
gh --version
```

```text
2.11.0
hcloud v1.70.2
Client Version: v1.36.1
gh version 2.102.1 (2026-10-20)
```

### 0.2 SSH key

Key yoksa oluşturun. Varsa bu adımı atlayın:

```bash
ls ~/.ssh/hetzner_k3s ~/.ssh/hetzner_k3s.pub
```

```bash
ssh-keygen -t ed25519 -f ~/.ssh/hetzner_k3s -N ""
```

### 0.3 Hetzner API token

Hetzner Console > `education` projesi > Security > API Tokens > Generate API Token, yetki **Read & Write**.

hcloud CLI'a token'ı bir kez verin. Komut token'ı gizli olarak sorar:

```bash
hcloud context create education
```

Token'ı hetzner-k3s için ortam değişkenine alın. Bu komutu her yeni terminalde tekrar çalıştırın:

```bash
export HCLOUD_TOKEN=$(sed -nE 's/.*token *= *"([^"]*)".*/\1/p' ~/.config/hcloud/cli.toml)
```

`cli.toml` içinde birden fazla context varsa bu komut birden fazla token döndürür. O durumda token'ı
elle verin: `export HCLOUD_TOKEN=<api-token>`.

Projenin boş olduğunu kontrol edin. Her satır `0` göstermeli:

```bash
hcloud context active
for r in server network firewall placement-group ssh-key primary-ip floating-ip load-balancer volume; do
  printf "%s: " $r
  hcloud $r list -o noheader | grep -c .
done
```

```text
education
server: 0
network: 0
firewall: 0
placement-group: 0
ssh-key: 0
primary-ip: 0
floating-ip: 0
load-balancer: 0
volume: 0
```

### 0.4 Kendi IP'nizi cluster.yaml'a yazın

SSH (22) portu sadece `cluster.yaml`'daki IP'ye açıktır. Eğitim yerindeki IP'niz farklı olabilir:

```bash
curl -s https://ifconfig.me
```

```text
78.162.65.17
```

IP farklıysa `cluster.yaml`'daki IP'yi yenisiyle değiştirin:

```bash
MY_IP=$(curl -s https://ifconfig.me)
sed -i '' "s|- 78.162.65.17/32|- $MY_IP/32|" hetzner-k3s/cluster.yaml
grep -A1 'ssh: # 22' hetzner-k3s/cluster.yaml
```

```text
    ssh: # 22 portuna sadece bu IP'ler erişebilir
      - 78.162.65.17/32
```

### 0.5 GitHub CLI girişi

```bash
gh auth status
```

`Logged in to github.com account Fcakiroglu16` görünmeli. Görünmüyorsa `gh auth login` çalıştırın.

---

## 1. Cluster'ı kurun

Kullanılan config: [hetzner-k3s/cluster.yaml](hetzner-k3s/cluster.yaml). 1 master, 2 worker, cx23, fsn1,
`addons.traefik.enabled: true`.

```bash
cd hetzner-k3s
hetzner-k3s create --config cluster.yaml | tee create.log
cd ..
```

Yaklaşık 6 dakika sürer. Son satırlar:

```text
[Cluster Autoscaler] Waiting for at least one worker node to be ready...
[Control plane] Switched to context "egitim-master1".
```

Araç `hetzner-k3s/kubeconfig` dosyasını oluşturur. kubectl'e bu dosyayı gösterin. Bu komutu her yeni
terminalde tekrar çalıştırın:

```bash
export KUBECONFIG=$PWD/hetzner-k3s/kubeconfig
```

```bash
kubectl get nodes
```

```text
NAME                         STATUS   ROLES                AGE   VERSION
egitim-master1               Ready    control-plane,etcd   4m    v1.37.2+k3s1
egitim-pool-worker-worker1   Ready    <none>               10s   v1.37.2+k3s1
egitim-pool-worker-worker2   Ready    <none>               21s   v1.37.2+k3s1
```

Ayrıntı: [hetzner-k3s.md, 6. ve 7. adım](hetzner-k3s/hetzner-k3s.md#6-clusterı-oluşturun)

---

## 2. Traefik'in Gateway API desteğini açın

Cluster kurulur kurulmaz uygulayın. Traefik henüz kurulmadıysa ilk kurulumda bu ayarlarla gelir.
Kurulduysa k3s onu bu ayarlarla yeniden kurar.

```bash
kubectl apply -f GatewayAPI/1-traefik-config.yaml
```

```text
helmchartconfig.helm.cattle.io/traefik created
```

Traefik'in kurulmasını bekleyin. Worker'lar yeni katıldıysa 1-2 dakika sürebilir:

```bash
kubectl -n kube-system wait --for=condition=complete job/helm-install-traefik --timeout=300s
kubectl -n kube-system rollout status deploy/traefik --timeout=180s
```

```text
job.batch/helm-install-traefik condition met
deployment "traefik" successfully rolled out
```

GatewayClass hazır mı:

```bash
kubectl get gatewayclass
```

```text
NAME      CONTROLLER                      ACCEPTED   AGE
traefik   traefik.io/gateway-controller   True       8s
```

Traefik'in LB'si oluştu mu. `EXTERNAL-IP` dolana kadar birkaç saniye bekleyin:

```bash
kubectl -n kube-system get svc traefik
hcloud load-balancer list
```

```text
NAME      TYPE           CLUSTER-IP    EXTERNAL-IP                                  PORT(S)
traefik   LoadBalancer   10.43.39.65   10.0.0.5,2.31.133.62,2a01:4f8:c016:c4c6::1   80:31366/TCP,443:31268/TCP

ID        NAME      HEALTH    IPV4          IPV6                    TYPE   LOCATION
8252084   traefik   healthy   2.31.133.62   2a01:4f8:c016:c4c6::1   lb11   fsn1
```

LB'nin IPv4 adresini bir değişkene alın. 5. adımda DNS kaydına bu IP yazılır:

```bash
LB_IP=$(hcloud load-balancer describe traefik -o format='{{.PublicNet.IPv4.IP}}')
echo $LB_IP
```

```text
2.31.133.62
```

**IP her kurulumda değişir.** Yukarıdaki IP bu testteki değerdir.

Ayrıntı: [gateway-api.md, 1. adım](GatewayAPI/gateway-api.md#1-traefikin-gateway-api-desteğini-açın)

---

## 3. Namespace, Gateway ve HTTPRoute

```bash
kubectl apply -f PrivateRegistry/1-namespace.yaml
kubectl apply -f GatewayAPI/2-gateway.yaml -f GatewayAPI/3-httproute.yaml
```

```text
namespace/week6 created
gateway.gateway.networking.k8s.io/week6-gateway created
httproute.gateway.networking.k8s.io/week6-api created
```

```bash
kubectl -n week6 get gateway,httproute
```

```text
NAME                                              CLASS     ADDRESS       PROGRAMMED
gateway.gateway.networking.k8s.io/week6-gateway   traefik   2.31.133.62   True

NAME                                            HOSTNAMES
httproute.gateway.networking.k8s.io/week6-api   ["order.kiteloomstudio.com"]
```

HTTPRoute'un yönlendirdiği `week6-api` Service'i henüz yok. Onu 4. adımda workflow oluşturur.
HTTPRoute'un Service'i bulduğu 4.4'te doğrulanır.

---

## 4. Uygulamayı GitHub Actions ile deploy edin

### 4.1 KUBE_CONFIG secret'ı

Cluster her kurulduğunda kubeconfig değişir. Secret'ı yeni dosyayla güncelleyin. Bunu yapmazsanız
workflow eski cluster'a bağlanmaya çalışır ve başarısız olur:

```bash
gh secret set KUBE_CONFIG < hetzner-k3s/kubeconfig
```

### 4.2 GHCR_PULL_TOKEN secret'ı

Bu secret cluster'a bağlı değildir, yeniden kurulumda değişmez. Sadece hiç eklenmemişse veya süresi
dolduysa ekleyin:

1. `read:packages` yetkili classic bir PAT oluşturun:
   https://github.com/settings/tokens/new?scopes=read:packages&description=k3s-ghcr-pull
2. Kaydedin. Komut token'ı gizli olarak sorar:

```bash
gh secret set GHCR_PULL_TOKEN
```

İki secret da listede olmalı. `KUBE_CONFIG`'in tarihi şimdiki zaman olmalı:

```bash
gh secret list
```

```text
GHCR_PULL_TOKEN	2026-11-15T23:20:51Z
KUBE_CONFIG	2026-11-16T14:56:15Z
```

### 4.3 Workflow'u çalıştırın

Workflow normalde `main`'e merge olunca çalışır. Kod değiştirmeden elle çalıştırmak için:

```bash
gh workflow run week6-api.yml --ref main
```

Çalışmayı izleyin:

```bash
sleep 5
gh run watch $(gh run list --workflow week6-api.yml -L1 --json databaseId -q '.[0].databaseId') --exit-status
```

Tüm adımlar `success` olmalı:

```bash
gh run view $(gh run list --workflow week6-api.yml -L1 --json databaseId -q '.[0].databaseId') --json jobs -q '.jobs[0].steps[] | .name+": "+.conclusion'
```

```text
Set up job: success
Run actions/checkout@v7: success
Image etiketi: success
Docker build ve push: success
Cluster'a bağlan: success
Image pull secret: success
Deploy: success
Post Run actions/checkout@v7: success
Complete job: success
```

Workflow sırayla şunları yapar: image'ı build edip GHCR'ye push eder, `ghcr-pull-secret`'ı oluşturur,
`2-deployment.yaml` ve `3-service.yaml`'ı uygular. Ayrıntı:
[private-registry.md, 8. adım](PrivateRegistry/private-registry.md#8-workflow)

### 4.4 Doğrulayın

```bash
kubectl -n week6 get deploy,pods,svc,secret
```

```text
NAME                        READY   UP-TO-DATE   AVAILABLE
deployment.apps/week6-api   3/3     3            3

NAME                             READY   STATUS    RESTARTS
pod/week6-api-5f854c79fb-7kq2k   1/1     Running   0
pod/week6-api-5f854c79fb-8kvxb   1/1     Running   0
pod/week6-api-5f854c79fb-qfcrn   1/1     Running   0

NAME                TYPE        CLUSTER-IP      EXTERNAL-IP   PORT(S)
service/week6-api   ClusterIP   10.43.206.242   <none>        80/TCP

NAME                      TYPE                             DATA
secret/ghcr-pull-secret   kubernetes.io/dockerconfigjson   1
```

HTTPRoute artık Service'i buluyor:

```bash
kubectl -n week6 get httproute week6-api -o jsonpath='{range .status.parents[0].conditions[*]}{.type}={.status}{"\n"}{end}'
kubectl -n week6 get gateway week6-gateway -o jsonpath='{range .status.listeners[*]}attachedRoutes={.attachedRoutes}{"\n"}{end}'
```

```text
Accepted=True
ResolvedRefs=True
attachedRoutes=1
```

DNS kaydı olmadan Gateway üzerinden test:

```bash
curl -H "Host: order.kiteloomstudio.com" http://$LB_IP/
```

```json
{"message":"Week6.API çalışıyor","version":"sha-ffa3359","pod":"week6-api-5f854c79fb-8kvxb"}
```

---

## 5. Cloudflare DNS kaydı

Cloudflare Dashboard > kiteloomstudio.com > DNS > Records altındaki `order` kaydını düzenleyin. Kayıt
yoksa **Add record** ile ekleyin:

| Alan | Değer |
|---|---|
| Type | `A` |
| Name | `order` |
| IPv4 address | 2. adımdaki `LB_IP` (bu testte `2.31.133.62`) |
| Proxy status | **DNS only** (gri bulut) |
| TTL | Auto |

**Save**'e basın. Neden DNS only: [gateway-api.md, Proxy açıksa](GatewayAPI/gateway-api.md#proxy-açıksa-turuncu-bulut)

Cloudflare'in kendi nameserver'ına sorun. Cevap `LB_IP` olmalı:

```bash
dig +short order.kiteloomstudio.com @rustam.ns.cloudflare.com
```

```text
2.31.133.62
```

Cevap eski IP ise kayıt kaydedilmemiştir. Cloudflare IP'leri (`188.114.x.x`, `104.21.x.x`) ise proxy
açıktır.

---

## 6. Alan adıyla erişin

```bash
curl http://order.kiteloomstudio.com/
curl http://order.kiteloomstudio.com/health
```

```text
{"message":"Week6.API çalışıyor","version":"sha-ffa3359","pod":"week6-api-5f854c79fb-8kvxb"}
Healthy
```

Alan adı olmadan, sadece IP ile gelen istek `404` alır. Gateway sadece `order.kiteloomstudio.com`'u kabul eder:

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://$LB_IP/
```

```text
404
```

İstekler 3 pod'a dağılır:

```bash
for i in $(seq 1 12); do curl -s http://order.kiteloomstudio.com/ | jq -r .pod; done | sort | uniq -c
```

```text
   4 week6-api-5f854c79fb-7kq2k
   3 week6-api-5f854c79fb-8kvxb
   5 week6-api-5f854c79fb-qfcrn
```

Tarayıcıdan: http://order.kiteloomstudio.com/

Kurulum tamamdır. Yeni sürüm yayınlamak için `PrivateRegistry/` altında bir değişikliği `main`'e
gönderin. Workflow kendiliğinden çalışır. Bkz.
[private-registry.md, 12. adım](PrivateRegistry/private-registry.md#12-yeni-sürüm-yayınlama)

---

## 7. Ders sonunda tüm kaynakları silin

Silme işlemi geri alınamaz. Cluster'daki tüm veriler kaybolur. Cloudflare'deki DNS kaydı silinmez,
sonraki kurulumda yeni IP ile güncellenir.

### 7.1 Ücretli uygulama kaynaklarını kontrol edin

`hetzner-k3s delete`, CCM'in açtığı LB'leri ve CSI'ın açtığı volume'ları silmez:

```bash
kubectl get svc -A --field-selector spec.type=LoadBalancer
kubectl get pvc -A
```

```text
NAMESPACE     NAME      TYPE           CLUSTER-IP    EXTERNAL-IP
kube-system   traefik   LoadBalancer   10.43.39.65   10.0.0.5,2.31.133.62,2a01:4f8:c016:c4c6::1

No resources found
```

Bu çıktıda tek LB `traefik`'tir, PVC yoktur.

PVC varsa (örneğin Lens'in kurduğu Prometheus, `lens-metrics` namespace'i), cluster silinmeden önce
namespace'ini silin. Böylece CSI Hetzner volume'unu da siler:

```bash
kubectl delete namespace lens-metrics
```

Traefik LB'sini k3s yönetir, `kubectl` ile silinirse geri gelir. Onu 7.4'te `hcloud` ile sileceğiz.

### 7.2 Silme korumasını kaldırın

```bash
sed -i '' 's/^protect_against_deletion: true/protect_against_deletion: false/' hetzner-k3s/cluster.yaml
grep '^protect_against_deletion' hetzner-k3s/cluster.yaml
```

```text
protect_against_deletion: false
```

### 7.3 Cluster'ı silin

Araç onay için cluster adını ister. `egitim` yazıp Enter'a basın:

```bash
cd hetzner-k3s
hetzner-k3s delete --config cluster.yaml | tee delete.log
cd ..
```

```text
Please enter the cluster name to confirm that you want to delete it: egitim
...
[Private network] ...private network deleted
[Firewall] ...firewall deleted.
[SSH key] ...SSH key deleted
```

Araç `hetzner-k3s/kubeconfig` dosyasını da siler.

### 7.4 Traefik LB'sini silin

```bash
hcloud load-balancer delete traefik
```

```text
Load Balancer traefik deleted
```

### 7.5 Projenin boş olduğunu doğrulayın

```bash
for r in server network firewall placement-group ssh-key primary-ip floating-ip load-balancer volume; do
  printf "%s: " $r
  hcloud $r list -o noheader | grep -c .
done
```

Her satır `0` göstermeli. `load-balancer` veya `volume` satırında başka bir sayı varsa ücretlendirme
devam eder:

```bash
hcloud load-balancer list
hcloud volume list
```

Listede kalanları adıyla silin. Örneğin `hcloud volume delete pvc-a7db5f9f-920e-4a7f-b596-fabd7159d48a`.

### 7.6 Silme korumasını geri açın

Sonraki kurulum için config'i eski haline getirin:

```bash
sed -i '' 's/^protect_against_deletion: false/protect_against_deletion: true/' hetzner-k3s/cluster.yaml
git -C .. status --short
```

`git status` çıktısı boş olmalı. `cluster.yaml` değişmiş görünüyorsa 0.4'teki IP değişikliğidir.

GitHub'daki `KUBE_CONFIG` secret'ı silinen cluster'a aittir ve artık işe yaramaz. Sonraki kurulumda
4.1'de güncellenir.
