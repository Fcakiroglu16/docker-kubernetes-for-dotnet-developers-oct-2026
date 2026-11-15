# GitHub Actions ile Private Registry'den k3s'e Deploy

Bu rehber, bir .NET API'yi her `main` merge'ünde otomatik olarak build edip
**GitHub Container Registry**'ye (GHCR) göndermeyi ve Hetzner'deki k3s cluster'ına deploy etmeyi anlatır.
Image private'tır. Kubernetes onu bir **image pull secret** ile çeker.

> Test edildi: hetzner-k3s v2.11.0, k3s v1.37.2+k3s1, .NET 10, GitHub Actions (ubuntu-latest).
> Ön koşul: [hetzner-k3s ile kurulmuş cluster](../hetzner-k3s/hetzner-k3s.md).

## Akış

```text
 git push main
      │
      ▼
┌─────────────────────────── GitHub Actions ───────────────────────────┐
│ build job                          deploy job                        │
│  1. checkout                        1. checkout                      │
│  2. docker build (linux/amd64)      2. kubectl kur                   │
│  3. GHCR'ye push ─────────┐         3. KUBE_CONFIG ile bağlan        │
│     sha-1a2b3c4, latest   │         4. ghcr-pull-secret oluştur      │
│                           │         5. deployment + service apply    │
│                           │         6. rollout status bekle          │
└───────────────────────────┼─────────────────────┬────────────────────┘
                            ▼                     │ 6443 (Kubernetes API)
                 ┌────────────────────┐           ▼
                 │ ghcr.io (private)  │◄── image çeker (ghcr-pull-secret ile)
                 │ week6-api          │    ┌──────────────────────────┐
                 └────────────────────┘    │ k3s cluster              │
                                           │ namespace: week6         │
                                           │ 3 pod  ◄── NodePort 30080│
                                           └──────────────────────────┘
```

İki ayrı kimlik kullanılır:

| Kimlik | Nerede | Ne için | Yetkisi |
|---|---|---|---|
| `GITHUB_TOKEN` | GitHub'ın her çalışmada otomatik ürettiği token | Image'ı GHCR'ye **göndermek** | Sadece bu repo, iş bitince geçersiz |
| `GHCR_PULL_TOKEN` | Sizin oluşturduğunuz PAT, GitHub secret'ı | Cluster'ın image'ı **çekmesi** | Sadece `read:packages` |
| `KUBE_CONFIG` | ServiceAccount token'ı içeren kubeconfig, GitHub secret'ı | Workflow'un cluster'a **deploy etmesi** | Sadece `week6` namespace'i |

## Klasör yapısı

```text
.github/workflows/week6-api.yml        # CI/CD workflow (repo kökünde olmak zorunda)
kubernetes/week-6/PrivateRegistry/
├── Week6.API/                          # .NET API projesi ve Dockerfile
├── 1-namespace.yaml                    # week6 namespace'i (elle, bir kez)
├── 2-github-deployer-rbac.yaml         # GitHub Actions'ın kimliği (elle, bir kez)
├── 3-deployment.yaml                   # workflow her çalışmada uygular
├── 4-service.yaml                      # workflow her çalışmada uygular
└── private-registry.md
```

`1` ve `2` numaralı dosyalar cluster yöneticisi tarafından bir kez uygulanır. Workflow'un
namespace oluşturma veya yetki verme izni yoktur, bu bilinçli bir kısıtlamadır.

---

## 1. API projesi

`Week6.API`, iki endpoint'i olan minimal bir API'dir:

| Endpoint | Cevap | Neden |
|---|---|---|
| `GET /` | `{"message", "version", "pod"}` | `version` hangi image sürümünün çalıştığını, `pod` hangi pod'un cevap verdiğini gösterir |
| `GET /health` | `Healthy` | Kubernetes readiness ve liveness probe'ları için |

Sürüm bilgisi image build edilirken verilir. Dockerfile'da:

```dockerfile
LABEL org.opencontainers.image.source="https://github.com/Fcakiroglu16/docker-kubernetes-for-dotnet-developers-oct-2026"
ARG APP_VERSION=dev
ENV APP_VERSION=$APP_VERSION
```

- `ARG APP_VERSION`: Build sırasında `--build-arg APP_VERSION=...` ile verilir. Workflow commit'e özel
  etiketi (`sha-1a2b3c4`) verir.
- `ENV APP_VERSION`: Değeri container'ın ortam değişkenine taşır. API bunu okuyup cevapta gösterir.
- `LABEL org.opencontainers.image.source`: Image'ı GitHub reposuna bağlar. GHCR paketi repo sayfasının
  sağındaki **Packages** bölümünde görünür. Bu label'ın görünürlüğe etkisi için [11. adıma](#11-paketi-private-yapın)
  bakın.

### Yerelde test

```bash
cd kubernetes/week-6/PrivateRegistry/Week6.API
```

```bash
docker build --platform linux/amd64 --build-arg APP_VERSION=1.0.0 -t week6-api:local .
```

```bash
docker run --rm -p 18080:8080 week6-api:local
```

Başka bir terminalde:

```bash
curl localhost:18080/
```

```json
{"message":"Week6.API çalışıyor","version":"1.0.0","pod":"ba284a5a1ca3"}
```

**Neden `--platform linux/amd64`?** Hetzner `cx23` sunucuları x86 (amd64) işlemcilidir. Apple Silicon
bir Mac'te platform verilmezse image arm64 olarak build edilir ve node'da `exec format error` ile
çalışmaz. Workflow'daki runner zaten amd64'tür, ama platformu yine de açıkça belirtir.

---

## 2. Kubernetes API'yi GitHub Actions'a açın

GitHub Actions runner'larının IP adresi her çalışmada değişir. Cluster kurulumunda 6443 portu sadece
kendi IP'nize açıktı. Bu haliyle workflow cluster'a bağlanamaz.

`hetzner-k3s/cluster.yaml` içinde API erişimini herkese açın:

```yaml
  allowed_networks:
    ssh:
      - 78.162.65.17/32 # SSH yine sadece sizin IP'nize açık
    api:
      - 0.0.0.0/0
      - ::/0
```

Firewall'u güncellemek için `create` komutunu tekrar çalıştırın. Sunuculara dokunulmaz:

```bash
cd kubernetes/week-6/hetzner-k3s
hetzner-k3s create --config cluster.yaml
```

Doğrulayın:

```bash
hcloud firewall describe egitim
```

6443 kuralının kaynağı `0.0.0.0/0, ::/0` olmalı, 22 kuralı ise hâlâ sadece sizin IP'niz.

**Bu güvenli mi?** API'ye ağ seviyesinde herkes ulaşabilir, ama geçerli bir token veya sertifika
olmadan hiçbir işlem yapılamaz. Bu, EKS ve GKE'deki "public endpoint" modeliyle aynıdır. Riski azaltmak
için workflow'a admin yetkisi değil, sadece tek bir namespace'e yetkili bir kimlik verilir (sonraki adımlar).

Daha güvenli alternatifler: workflow'un runner IP'sini geçici olarak firewall'a eklemesi, ya da
cluster içinde self-hosted runner çalıştırmak. İkisi de daha karmaşıktır.

---

## 3. Namespace

```bash
cd kubernetes/week-6/PrivateRegistry
export KUBECONFIG=$PWD/../hetzner-k3s/kubeconfig
```

```bash
kubectl apply -f 1-namespace.yaml
```

[`1-namespace.yaml`](1-namespace.yaml) `week6` namespace'ini oluşturur.

**Neden ayrı namespace?**

- Workflow'un yetkisi sadece bu namespace ile sınırlanır.
- Secret'lar namespace'e aittir. `ghcr-pull-secret` sadece `week6`'daki pod'lar tarafından kullanılabilir.

---

## 4. GitHub Actions için kimlik (ServiceAccount + RBAC)

```bash
kubectl apply -f 2-github-deployer-rbac.yaml
```

[`2-github-deployer-rbac.yaml`](2-github-deployer-rbac.yaml) dört kaynak oluşturur:

| Kaynak | Ne yapar |
|---|---|
| `ServiceAccount github-deployer` | Workflow'un cluster'daki kimliği |
| `Role github-deployer` | `week6` namespace'inde hangi işlemlere izin verildiği |
| `RoleBinding github-deployer` | Role'ü ServiceAccount'a bağlar |
| `Secret github-deployer-token` | ServiceAccount için süresi dolmayan bir token. Kubernetes token'ı bu secret'a kendisi yazar. |

Role'deki izinler ve nedenleri:

| Kaynak | İzinler | Neden |
|---|---|---|
| `deployments`, `replicasets` | get, list, watch, create, update, patch | `kubectl apply` ve `kubectl rollout status` |
| `services` | get, list, create, update, patch | Service'i oluşturmak ve güncellemek |
| `secrets` | get, create, update, patch | `ghcr-pull-secret`'ı oluşturmak ve güncellemek |
| `pods`, `events` | get, list, watch | Rollout takılırsa sorunu görmek |

Bilinçli olarak verilmeyen izinler: `delete`, secret'ları **listeleme**, başka namespace'ler ve cluster
geneli kaynaklar (node, namespace). Token sızarsa saldırgan sadece `week6`'daki uygulamayı
değiştirebilir, cluster'ı ele geçiremez.

### Doğrulama

`kubectl auth can-i` ile ServiceAccount'un yetkilerini test edin:

```bash
SA=system:serviceaccount:week6:github-deployer
kubectl auth can-i patch deployments -n week6 --as=$SA
kubectl auth can-i delete deployments -n week6 --as=$SA
kubectl auth can-i get pods -n kube-system --as=$SA
```

| Komut | Sonuç |
|---|---|
| `patch deployments -n week6` | yes |
| `create secrets -n week6` | yes |
| `list pods -n week6` | yes |
| `delete deployments -n week6` | no |
| `list secrets -n week6` | no |
| `get pods -n kube-system` | no |
| `create namespaces` | no |
| `list nodes` | no |

---

## 5. KUBE_CONFIG secret'ı

Workflow'un cluster'a bağlanması için, ServiceAccount token'ını kullanan bir kubeconfig oluşturun.
Admin kubeconfig'i (`hetzner-k3s/kubeconfig`) GitHub'a **koymayın**, o dosya cluster'a tam yetki verir.

```bash
OUT=/tmp/github-deployer.kubeconfig
SERVER=$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')
kubectl -n week6 get secret github-deployer-token -o jsonpath='{.data.ca\.crt}' | base64 -d > /tmp/ca.crt
TOKEN=$(kubectl -n week6 get secret github-deployer-token -o jsonpath='{.data.token}' | base64 -d)

KUBECONFIG=$OUT kubectl config set-cluster egitim --server=$SERVER --certificate-authority=/tmp/ca.crt --embed-certs=true
KUBECONFIG=$OUT kubectl config set-credentials github-deployer --token=$TOKEN
KUBECONFIG=$OUT kubectl config set-context github-deployer --cluster=egitim --user=github-deployer --namespace=week6
KUBECONFIG=$OUT kubectl config use-context github-deployer
```

| Parça | Nereden gelir | Ne işe yarar |
|---|---|---|
| `server` | Admin kubeconfig | API adresi: `https://<master-public-ip>:6443` |
| `ca.crt` | Token secret'ı | Cluster'ın sertifikasını doğrulamak için. Araya giren sahte bir sunucuyu engeller. |
| `token` | Token secret'ı | ServiceAccount'un kimliği |

Gerçek token ile test edin:

```bash
KUBECONFIG=$OUT kubectl auth whoami
KUBECONFIG=$OUT kubectl get pods -n kube-system
```

```text
Username    system:serviceaccount:week6:github-deployer
Error from server (Forbidden): pods is forbidden: User "system:serviceaccount:week6:github-deployer"
cannot list resource "pods" in API group "" in the namespace "kube-system"
```

GitHub'a secret olarak kaydedin ve yerel dosyaları silin:

```bash
gh secret set KUBE_CONFIG -R Fcakiroglu16/docker-kubernetes-for-dotnet-developers-oct-2026 < $OUT
rm $OUT /tmp/ca.crt
```

Tarayıcıdan da eklenebilir: Repo > Settings > Secrets and variables > Actions > New repository secret.

---

## 6. GHCR_PULL_TOKEN secret'ı

Cluster'ın private image'ı çekebilmesi için kalıcı bir token gerekir.

**Neden `GITHUB_TOKEN` yetmiyor?** `GITHUB_TOKEN` her workflow çalışması için üretilir ve iş bitince
geçersiz olur. Cluster ise image'ı deploy'dan saatler sonra da çekebilir: pod yeniden başlarsa, başka
node'a taşınırsa veya yeni node eklenirse. O anda token geçersiz olduğu için pod açılamaz.

1. Sadece `read:packages` yetkisi olan classic bir PAT oluşturun:
   https://github.com/settings/tokens/new?scopes=read:packages&description=k3s-ghcr-pull
2. Expiration için bir süre seçin. Süre dolunca token'ı yenileyip secret'ı güncellemeniz gerekir.
3. Token'ı GitHub secret'ı olarak kaydedin. Komut token'ı gizli olarak sorar:

```bash
gh secret set GHCR_PULL_TOKEN -R Fcakiroglu16/docker-kubernetes-for-dotnet-developers-oct-2026
```

**Neden sadece `read:packages`?** Token cluster'da bir secret olarak durur. Sızarsa sadece image okunabilir.
`repo` veya `write:packages` yetkisi olsaydı, kodunuzu değiştirmek veya zararlı bir image yüklemek
mümkün olurdu.

Secret'ları kontrol edin:

```bash
gh secret list
```

```text
GHCR_PULL_TOKEN
KUBE_CONFIG
```

---

## 7. Deployment

[`3-deployment.yaml`](3-deployment.yaml)

```yaml
spec:
  replicas: 3
  strategy:
    type: RollingUpdate
    rollingUpdate:
      maxSurge: 1
      maxUnavailable: 0
  template:
    spec:
      imagePullSecrets:
        - name: ghcr-pull-secret
      containers:
        - name: week6-api
          image: ghcr.io/fcakiroglu16/week6-api:latest
          lifecycle:
            preStop:
              sleep:
                seconds: 5
```

| Ayar | Neden |
|---|---|
| `replicas: 3` | 3 worker'a birer pod düşer |
| `imagePullSecrets` | kubelet image'ı çekerken `ghcr-pull-secret`'taki kullanıcı adı ve token'ı kullanır |
| `maxSurge: 1`, `maxUnavailable: 0` | Yeni sürümde önce yeni pod açılır, hazır olunca eskisi kapanır. Kapasite hiç düşmez. |
| `image: ...:latest` | Workflow bu etiketi commit'e özel etiketle (`sha-1a2b3c4`) değiştirip uygular |
| `readinessProbe` | Pod `/health` cevap verene kadar trafik almaz. Rolling update bunu bekler. |
| `livenessProbe` | `/health` cevap vermezse container yeniden başlatılır |
| `resources` | Scheduler pod'u yeterli kaynağı olan node'a yerleştirir |
| `lifecycle.preStop.sleep` | Pod kapanmadan önce 5 saniye bekler. Rolling update sırasında istek kaybını önler, bkz. [13.3](#133-kesintisiz-geçiş-prestop). |

**Neden `latest` yerine commit etiketi?** `latest` her push'ta başka bir image'ı gösterir.
Deployment'ta `latest` kalsaydı:

- Etiket değişmediği için `kubectl apply` hiçbir şeyi değiştirmez ve rollout başlamaz.
- Hangi pod'un hangi kodu çalıştırdığı belli olmaz.
- Geri almak (`kubectl rollout undo`) için önceki sürüm bilinmez.

`sha-1a2b3c4` etiketi tek bir commit'i gösterir ve hiç değişmez.

---

## 8. Service

[`4-service.yaml`](4-service.yaml)

```yaml
spec:
  type: NodePort
  selector:
    app: week6-api
  ports:
    - port: 80
      targetPort: 8080
      nodePort: 30080
```

- `type: NodePort`: Uygulama her node'un public IP'sinin `30080` portundan erişilebilir olur.
  hetzner-k3s firewall'u 30000-32767 aralığını açık tutar.
- `targetPort: 8080`: .NET container'ının dinlediği port (`EXPOSE 8080`).
- `LoadBalancer` tipi neden kullanılmadı? Hetzner'de ücretli bir Load Balancer açar (aylık ~7,89 €).
  Test için NodePort yeterlidir.

---

## 9. Workflow

[`.github/workflows/week6-api.yml`](../../../.github/workflows/week6-api.yml). Her `uses:` satırının
üstünde ne işe yaradığını anlatan bir yorum var.

### Ne zaman çalışır?

```yaml
on:
  push:
    branches: [main]
    paths:
      - "kubernetes/week-6/PrivateRegistry/**"
      - "!kubernetes/week-6/PrivateRegistry/**/*.md"
      - ".github/workflows/week6-api.yml"
  workflow_dispatch:
```

- `push` + `branches: [main]`: Bir PR main'e merge edildiğinde veya main'e doğrudan push yapıldığında.
- `paths`: Sadece bu API veya workflow değiştiyse. Repodaki diğer haftaların değişiklikleri deploy tetiklemez.
- `!**/*.md`: Sadece doküman değiştiyse deploy yapılmaz.
- `workflow_dispatch`: Actions sekmesinden elle çalıştırmak için.

### Diğer ayarlar

```yaml
permissions:
  contents: read
  packages: write
```

`GITHUB_TOKEN`'ın yetkileri. Kod okunur, GHCR'ye image yazılır. Başka hiçbir yetki verilmez.

```yaml
concurrency:
  group: week6-api-deploy
  cancel-in-progress: false
```

Art arda iki merge olursa iki deploy aynı anda çalışmaz. İkincisi birincinin bitmesini bekler.

### build job

| Adım | Ne yapar |
|---|---|
| `actions/checkout` | Kodu runner'a indirir |
| Image etiketi | Commit hash'inin ilk 7 karakterinden `sha-1a2b3c4` etiketi üretir |
| `docker/setup-buildx-action` | Buildx'i kurar (platform seçimi ve cache için) |
| `docker/login-action` | `GITHUB_TOKEN` ile `ghcr.io`'ya giriş yapar |
| `docker/build-push-action` | Image'ı build eder, `sha-1a2b3c4` ve `latest` etiketleriyle push eder |

### deploy job

`needs: build` sayesinde build başarılı olmadan çalışmaz.

| Adım | Ne yapar |
|---|---|
| `actions/checkout` | Yaml dosyaları için kodu tekrar indirir. Her job ayrı makinede çalışır. |
| `azure/setup-kubectl` | kubectl'i kurar |
| kubeconfig | `KUBE_CONFIG` secret'ını `~/.kube/config` dosyasına yazar. `kubectl auth whoami` ile bağlantıyı test eder. |
| Image pull secret | `GHCR_PULL_TOKEN` ile `ghcr-pull-secret`'ı oluşturur veya günceller |
| Deploy | `latest` etiketini `sha-...` ile değiştirir, yaml'ları uygular, rollout'un bitmesini bekler |
| Durum | Sonuç ne olursa olsun deployment, pod ve service durumunu log'a yazar |

Pull secret her çalışmada yeniden uygulanır:

```bash
kubectl create secret docker-registry ghcr-pull-secret \
  --namespace week6 \
  --docker-server=ghcr.io \
  --docker-username=fcakiroglu16 \
  --docker-password="$GHCR_PULL_TOKEN" \
  --dry-run=client -o yaml | kubectl apply -f -
```

- `--dry-run=client -o yaml`: Secret'ı cluster'a göndermeden yaml olarak üretir.
- `| kubectl apply -f -`: Yaml'ı uygular. Secret yoksa oluşturur, varsa günceller. `kubectl create`
  tek başına secret varsa hata verirdi.
- Token'ı yenilediğinizde sadece GitHub secret'ını güncellemeniz yeterli. Bir sonraki deploy cluster'daki
  secret'ı da günceller.

Secret yaml olarak bu klasörde **yoktur**, çünkü token içerir ve git'e girmemelidir. Üretilen secret
şu yapıdadır:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: ghcr-pull-secret
  namespace: week6
type: kubernetes.io/dockerconfigjson
data:
  .dockerconfigjson: <base64: {"auths":{"ghcr.io":{"username":"...","password":"...","auth":"..."}}}>
```

Deploy adımındaki `sed`:

```bash
sed -i "s|ghcr.io/fcakiroglu16/week6-api:latest|ghcr.io/fcakiroglu16/week6-api:sha-1a2b3c4|" 3-deployment.yaml
```

Dosya sadece runner'da değişir, repoya geri yazılmaz.

---

## 10. İlk çalıştırma ve doğrulama

Değişiklikleri main'e push edin:

```bash
git add .github/workflows/week6-api.yml kubernetes/week-6/PrivateRegistry
git commit -m "Add Week6.API CI/CD"
git push origin main
```

Workflow'u izleyin:

```bash
gh run watch
```

```text
✓ build in 48s
✓ deploy in 20s
```

Deploy log'unda kimliğin ServiceAccount olduğu ve pull secret'ın oluştuğu görünür:

```text
Username    system:serviceaccount:week6:github-deployer
secret/ghcr-pull-secret created
deployment "week6-api" successfully rolled out
```

Cluster'da:

```bash
kubectl -n week6 get deploy,pods -o wide
```

```text
NAME                        READY   UP-TO-DATE   AVAILABLE   IMAGES
deployment.apps/week6-api   3/3     3            3           ghcr.io/fcakiroglu16/week6-api:sha-45c0c53

NAME                            READY   STATUS    NODE
pod/week6-api-fdf497d79-dfq2p   1/1     Running   egitim-pool-worker-worker3
pod/week6-api-fdf497d79-nt2t4   1/1     Running   egitim-pool-worker-worker1
pod/week6-api-fdf497d79-s52nv   1/1     Running   egitim-pool-worker-worker2
```

Pull secret'ın tipi ve deployment'a bağlı olduğu:

```bash
kubectl -n week6 get secret ghcr-pull-secret
kubectl -n week6 get deploy week6-api -o jsonpath='{.spec.template.spec.imagePullSecrets}'
```

```text
ghcr-pull-secret   kubernetes.io/dockerconfigjson   1
[{"name":"ghcr-pull-secret"}]
```

API'yi herhangi bir node'un public IP'si üzerinden çağırın:

```bash
curl http://<node-public-ip>:30080/
```

```json
{"message":"Week6.API çalışıyor","version":"sha-45c0c53","pod":"week6-api-fdf497d79-s52nv"}
```

`version` alanı deploy edilen commit'i gösterir. Master'ın IP'si de cevap verir, çünkü NodePort her
node'da açılır. İstek pod'un çalıştığı worker'a cluster içinden yönlendirilir.

---

## 11. Paketi private yapın

**Dikkat:** İlk push'tan sonra paket **public** oldu. Dockerfile'daki
`org.opencontainers.image.source` label'ı paketi repoya bağlar ve paket, repo public olduğu için
public görünürlükle oluşur. Bu durumda herkes image'ı token olmadan çekebilir ve pull secret işlevsiz kalır.

Token olmadan çekilebiliyor mu kontrol edin:

```bash
T=$(curl -s "https://ghcr.io/token?scope=repository:fcakiroglu16/week6-api:pull" | jq -r .token)
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $T" \
  -H "Accept: application/vnd.oci.image.index.v1+json" \
  https://ghcr.io/v2/fcakiroglu16/week6-api/manifests/latest
```

`200` dönüyorsa paket public'tir.

Görünürlüğü değiştirmek için bir API yoktur, arayüzden yapılır:

1. https://github.com/users/Fcakiroglu16/packages/container/package/week6-api
2. **Package settings** > **Danger Zone** > **Change visibility** > **Private**
3. Onay için paket adını yazın.

Aynı kontrolü tekrar yapın. Artık anonim token bile verilmez:

```text
{"errors":[{"code":"UNAUTHORIZED","message":"authentication required"}]}
403
```

---

## 12. Pull secret'ın etkisini test edin

Aynı image'ı secret olmadan ve secret ile çeken iki pod açın. `latest` etiketini kullanın, nedeni
aşağıdaki nottadır.

Secret olmadan:

```bash
kubectl -n week6 run pull-no-secret --image=ghcr.io/fcakiroglu16/week6-api:latest --restart=Never
```

Secret ile:

```bash
kubectl -n week6 run pull-with-secret --image=ghcr.io/fcakiroglu16/week6-api:latest --restart=Never \
  --overrides='{"spec":{"imagePullSecrets":[{"name":"ghcr-pull-secret"}]}}'
```

```bash
kubectl -n week6 get pods pull-no-secret pull-with-secret
```

```text
NAME               READY   STATUS             RESTARTS
pull-no-secret     0/1     ImagePullBackOff   0
pull-with-secret   1/1     Running            0
```

Secret'sız pod'un hatası:

```bash
kubectl -n week6 describe pod pull-no-secret | grep -A2 Failed
```

```text
Failed to pull image "ghcr.io/fcakiroglu16/week6-api:latest": ... failed to resolve reference
"ghcr.io/fcakiroglu16/week6-api:latest": not found
```

Hata mesajı `unauthorized` değil `not found` der. GHCR, yetkisiz isteklere private paketin var olup
olmadığını bile söylemez. `not found` hatası görüp image adı doğruysa, ilk şüphelenmeniz gereken
pull secret'tır.

Durum geçişi:

1. `ErrImagePull`: Çekme denendi ve başarısız oldu.
2. `ImagePullBackOff`: kubelet tekrar denemeden önce giderek artan sürelerle bekliyor.

Test pod'larını silin:

```bash
kubectl -n week6 delete pod pull-no-secret pull-with-secret
```

### Not: k3s embedded registry mirror

hetzner-k3s ile kurulan bu cluster'da k3s **embedded registry mirror** (Spegel) açıktır. Bunu
node'daki `/etc/rancher/k3s/registries.yaml` dosyasından ve k3s servisindeki `--embedded-registry`
parametresinden görebilirsiniz. Bu özellik, bir node'da bulunan image'ı diğer node'lara ağ üzerinden dağıtır.

Önemli sonucu: **Cluster'da herhangi bir node'da zaten bulunan bir image, secret olmadan da
çekilebilir.** Test sırasında `sha-45c0c53` etiketi ile açılan secret'sız pod, `imagePullPolicy: Always`
olmasına rağmen `Running` oldu, çünkü image deployment'ın pod'ları tarafından node'lara çoktan
indirilmişti. Yukarıdaki testte `latest` etiketinin kullanılmasının nedeni budur: o etiket henüz
hiçbir node'da yoktu ve istek GHCR'ye gitmek zorunda kaldı.

Bu şu anlama gelir: Private image bir kez cluster'a girdiyse, aynı cluster'daki herhangi bir
namespace'teki herhangi bir pod onu secret olmadan çalıştırabilir. Pull secret, image'ın cluster'a
**ilk** girişini korur, cluster içindeki erişimi korumaz. Cluster'ı farklı ekiplerle paylaşıyorsanız
bu özelliği kapatmayı değerlendirin.

---

## 13. Yeni sürüm yayınlama

Yeni sürüm için ayrı bir işlem gerekmez. API kodunda veya yaml'larda yapılan değişiklik main'e merge
edildiğinde workflow çalışır, yeni commit etiketiyle image üretir ve deployment'ı günceller.

### 13.1 Değişikliği main'e gönderin

```bash
git commit -am "Week6.API: yeni özellik"
git push origin main
```

PR ile çalışıyorsanız, PR'ı main'e merge etmek aynı sonucu verir.

Elle tetiklemek için (örneğin kod değişmeden yeniden deploy):

```bash
gh workflow run week6-api.yml --ref main
```

Not: Elle tetikleme aynı commit üzerinde çalışırsa etiket de aynı kalır (`sha-...`). Deployment
değişmediği için rollout başlamaz. Yeni pod'lar istiyorsanız `kubectl rollout restart` kullanın.

### 13.2 Ne oldu?

```bash
kubectl -n week6 get deploy week6-api -o wide
kubectl -n week6 rollout history deploy/week6-api
```

```text
READY   IMAGES
3/3     ghcr.io/fcakiroglu16/week6-api:sha-605a99a

REVISION  CHANGE-CAUSE
1         <none>
2         <none>
```

Her deploy yeni bir revision oluşturur. Önceki sürüme dönmek için:

```bash
kubectl -n week6 rollout undo deploy/week6-api
```

Bu komut önceki revision'ın image etiketine (`sha-45c0c53`) döner. Etiketler değişmediği için
geri dönülen image, o commit'in birebir aynısıdır. Not: Bir sonraki main merge'ü tekrar en son
sürümü deploy eder.

Geçişi API üzerinden izleyebilirsiniz. Rolling update sırasında cevaplar eski ve yeni sürüm arasında
karışık gelir, sonra hepsi yeni sürüme geçer:

```bash
while true; do curl -s http://<node-public-ip>:30080/ | jq -r .version; sleep 0.5; done
```

```text
sha-45c0c53
sha-45c0c53
sha-605a99a
sha-45c0c53
sha-605a99a
sha-605a99a
```

### 13.3 Kesintisiz geçiş: preStop

İlk testte, `preStop` olmadan yapılan bir rollout sırasında 102 istekten 1'i başarısız oldu
(`curl` zaman aşımı). `maxUnavailable: 0` olmasına rağmen bu olur. Nedeni, pod kapanırken iki işin
**aynı anda** başlamasıdır:

1. kubelet container'a SIGTERM gönderir. .NET uygulaması hemen yeni bağlantı almayı bırakır.
2. Pod Service endpoint'lerinden çıkarılır. Her node'daki kube-proxy bu değişikliği birkaç yüz
   milisaniye ile birkaç saniye arasında bir gecikmeyle öğrenir.

İkincisi birinciden geç kalırsa, kube-proxy kapanmış pod'a istek göndermeye devam eder ve istek başarısız olur.

Çözüm, SIGTERM'den önce kısa bir bekleme eklemektir:

```yaml
          lifecycle:
            preStop:
              sleep:
                seconds: 5
```

Pod kapanırken önce `preStop` çalışır, 5 saniye beklenir, sonra SIGTERM gönderilir. Bu sürede pod
endpoint'lerden çıkmış ve kube-proxy'ler güncellenmiş olur. `sleep` aksiyonu Kubernetes'in kendi
özelliğidir, image'da `sleep` komutu olmasını gerektirmez.

Bu bekleme `terminationGracePeriodSeconds` (varsayılan 30 saniye) süresinin içinden düşer.

**Dikkat:** `preStop` sadece kendisiyle **birlikte oluşturulan** pod'lara uygulanır. `preStop`'u
ekleyen deploy sırasında kapanan pod'lar eski şablondan geldiği için bu deploy'da yine istek kaybı
olabilir. Testte bu deploy sırasında 145 istekten 3'ü başarısız oldu. Sonraki rollout'larda hata
görülmedi:

| Test | preStop | Başarısız / toplam istek |
|---|---|---|
| Workflow ile deploy (`sha-45c0c53` → `sha-605a99a`) | yok | 1 / 102 |
| preStop'u ekleyen deploy (`sha-605a99a` → `sha-51bb46f`) | yeni pod'larda var, kapanan pod'larda yok | 3 / 145 |
| 2 × `kubectl rollout restart` | var | 0 / 144 |

---

## Sorun giderme

| Belirti | Olası neden | Kontrol |
|---|---|---|
| Pod `ImagePullBackOff`, mesaj `not found` | Pull secret yok, yanlış veya token süresi dolmuş | `kubectl -n week6 get secret ghcr-pull-secret`, PAT'in süresi |
| Pod `ImagePullBackOff`, mesaj `exec format error` veya `no match for platform` | Image arm64 build edilmiş | Build'de `--platform linux/amd64` |
| Workflow `kubeconfig` adımında zaman aşımı | 6443 portu GitHub'a kapalı | `hcloud firewall describe egitim` |
| Workflow `Forbidden` hatası | Role'de gerekli izin yok | `kubectl auth can-i <fiil> <kaynak> -n week6 --as=system:serviceaccount:week6:github-deployer` |
| Workflow `GHCR_PULL_TOKEN secret'ı tanımlı değil` | Secret eklenmemiş | `gh secret list` |
| Build adımında `denied: permission_denied` | Workflow'da `packages: write` yok | `permissions` bloğu |
| `rollout status` zaman aşımı | Pod `/health` probe'unu geçemiyor | `kubectl -n week6 describe pod <pod>` |

## Temizlik

Uygulamayı silmek için (admin kubeconfig ile):

```bash
kubectl delete namespace week6
```

Namespace silinince içindeki deployment, service, pull secret, ServiceAccount ve token da silinir.
GitHub'daki `KUBE_CONFIG` secret'ı geçersiz hale gelir.

GitHub tarafı:

```bash
gh secret delete KUBE_CONFIG
gh secret delete GHCR_PULL_TOKEN
```

PAT'i https://github.com/settings/tokens sayfasından silin. GHCR paketini Package settings > Danger Zone >
Delete this package ile silebilirsiniz.

API erişimini tekrar kendi IP'nize kısıtlamak için `hetzner-k3s/cluster.yaml` içindeki
`allowed_networks.api` değerini geri alıp `create` komutunu çalıştırın.
