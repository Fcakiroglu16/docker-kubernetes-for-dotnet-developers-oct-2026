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
┌──────────────── GitHub Actions: build-and-deploy job ────────────────┐
│  1. checkout                                                         │
│  2. image etiketi: sha-1a2b3c4                                       │
│  3. docker build + push ──┐                                          │
│  4. KUBE_CONFIG ile bağlan│                                          │
│  5. ghcr-pull-secret oluştur                                         │
│  6. deployment + service apply, rollout bekle                        │
└───────────────────────────┼─────────────────────┬────────────────────┘
                            ▼                     │ 6443 (Kubernetes API)
                 ┌────────────────────┐           ▼
                 │ ghcr.io (private)  │◄── image çeker (ghcr-pull-secret ile)
                 │ week6-api          │    ┌──────────────────────────┐
                 └────────────────────┘    │ k3s cluster              │
                                           │ namespace: week6         │
                                           │ 3 pod                    │
                                           └────────────▲─────────────┘
                                                        │ private network
                                           ┌────────────┴─────────────┐
           Kullanıcı ─────────────────────►│ Traefik LB :80 (Gateway) │
  http://order.kiteloomstudio.com/         └──────────────────────────┘
```

Üç ayrı kimlik kullanılır:

| Kimlik | Nerede | Ne için | Yetkisi |
|---|---|---|---|
| `GITHUB_TOKEN` | GitHub'ın her çalışmada otomatik ürettiği token | Image'ı GHCR'ye **göndermek** | Sadece bu repo, iş bitince geçersiz |
| `GHCR_PULL_TOKEN` | Sizin oluşturduğunuz PAT, GitHub secret'ı | Cluster'ın image'ı **çekmesi** | Sadece `read:packages` |
| `KUBE_CONFIG` | Cluster'ın admin kubeconfig'i, GitHub secret'ı | Workflow'un cluster'a **deploy etmesi** | Cluster'ın tamamı (admin), bkz. [4. adım](#4-kube_config-secretı) |

## Klasör yapısı

```text
.github/workflows/week6-api.yml        # CI/CD workflow (repo kökünde olmak zorunda)
kubernetes/week-6/PrivateRegistry/
├── Week6.API/                          # .NET API projesi ve Dockerfile
├── 1-namespace.yaml                    # week6 namespace'i (elle, bir kez)
├── 2-deployment.yaml                   # workflow her çalışmada uygular
├── 3-service.yaml                      # workflow her çalışmada uygular
└── private-registry.md
```

`1-namespace.yaml` bir kez elle uygulanır. `2` ve `3` numaralı dosyaları workflow her çalışmada uygular.

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
  sağındaki **Packages** bölümünde görünür. Bu label'ın görünürlüğe etkisi için [10. adıma](#10-paketi-private-yapın)
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
çalışmaz. Workflow'daki `ubuntu-latest` runner zaten amd64 olduğu için orada `--platform` gerekmez.

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
olmadan hiçbir işlem yapılamaz. Bu, EKS ve GKE'deki "public endpoint" modeliyle aynıdır. Ancak bu
rehberde workflow admin kimliği kullandığı için o kimliğin sızması tüm cluster'ı açığa çıkarır.
Bkz. [4. adımdaki risk notu](#risk-bu-yol-sadece-eğitim-içindir).

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

- Uygulamanın tüm kaynakları tek yerde toplanır. Silmek tek komuttur: `kubectl delete namespace week6`.
- Secret'lar namespace'e aittir. `ghcr-pull-secret` sadece `week6`'daki pod'lar tarafından kullanılabilir.

---

## 4. KUBE_CONFIG secret'ı

Workflow cluster'a, hetzner-k3s'in kurulumda oluşturduğu **admin kubeconfig** ile bağlanır. Dosyayı
GitHub'a secret olarak kaydedin:

```bash
gh secret set KUBE_CONFIG -R Fcakiroglu16/docker-kubernetes-for-dotnet-developers-oct-2026 < ../hetzner-k3s/kubeconfig
```

Tarayıcıdan da eklenebilir: Repo > Settings > Secrets and variables > Actions > New repository secret.
Name `KUBE_CONFIG`, Secret alanına `kubeconfig` dosyasının tüm içeriği.

Kubeconfig üç bilgi içerir:

| Parça | Ne işe yarar |
|---|---|
| `server` | API adresi: `https://<master-public-ip>:6443` |
| `certificate-authority-data` | Cluster'ın sertifikasını doğrulamak için. Araya giren sahte bir sunucuyu engeller. |
| `client-certificate-data`, `client-key-data` | Kimlik. Bu cluster'da `system:admin` kullanıcısı. |

Bu kimliği kendi makinenizde görebilirsiniz:

```bash
KUBECONFIG=../hetzner-k3s/kubeconfig kubectl auth whoami
```

```text
Username    system:admin
Groups      [system:masters system:authenticated]
```

`system:masters` grubu cluster'da her şeye yetkilidir.

Cluster'ı silip yeniden kurarsanız kubeconfig değişir. Secret'ı yeni dosya ile tekrar kaydedin, yoksa
workflow bağlanamaz.

### Risk: Bu yol sadece eğitim içindir

Admin kubeconfig'i CI/CD'ye vermek pratiktir, ama production'da yapılmaz:

- **Tam yetki:** Dosyaya sahip olan herkes cluster'daki tüm secret'ları okuyabilir, node'lara istediği
  pod'u yerleştirebilir, cluster'ı silebilir.
- **API internete açık:** 6443 portu herkese açık olduğu için sızan dosya her yerden kullanılabilir.
- **İptal etmek zor:** Kimlik bir client sertifikasıdır. Bir token gibi tek komutla iptal edilemez.
  Sızdığından şüphelenirseniz eğitim cluster'ı için en basit çözüm, cluster'ı silip yeniden kurmaktır.
- **Public repo:** Workflow log'ları herkese açıktır. Secret'lar log'da `***` olarak maskelenir, ama
  workflow'a dosyayı yazdıran bir adım eklemek (örneğin `cat ~/.kube/config`) onu açığa çıkarabilir.

Production'da workflow'a sadece kendi namespace'inde deploy yapabilen ayrı bir kimlik verilir
(ServiceAccount + RBAC). Bu konu RBAC bölümünde anlatılır.

---

## 5. GHCR_PULL_TOKEN secret'ı

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

## 6. Deployment

[`2-deployment.yaml`](2-deployment.yaml)

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
| `lifecycle.preStop.sleep` | Pod kapanmadan önce 5 saniye bekler. Rolling update sırasında istek kaybını önler, bkz. [12.3](#123-kesintisiz-geçiş-prestop). |

**Neden `latest` yerine commit etiketi?** `latest` her push'ta başka bir image'ı gösterir.
Deployment'ta `latest` kalsaydı:

- Etiket değişmediği için `kubectl apply` hiçbir şeyi değiştirmez ve rollout başlamaz.
- Hangi pod'un hangi kodu çalıştırdığı belli olmaz.
- Geri almak (`kubectl rollout undo`) için önceki sürüm bilinmez.

`sha-1a2b3c4` etiketi tek bir commit'i gösterir ve hiç değişmez.

---

## 7. Service (ClusterIP)

[`3-service.yaml`](3-service.yaml)

```yaml
spec:
  type: ClusterIP
  selector:
    app: week6-api
  ports:
    - port: 80
      targetPort: 8080
```

`ClusterIP` Service'e sadece cluster içinden ulaşılır. Kubernetes ona sabit bir iç IP verir
(`10.43.x.x`). Bu IP'ye gelen istekleri kube-proxy `app: week6-api` label'lı pod'lardan birine iletir.

Dışarıdan gelen trafik bu Service'e doğrudan değil, **Gateway** üzerinden gelir:

```text
İstemci ──► Traefik LB 2.31.140.127:80 ──► Traefik pod ──► Service week6-api:80 ──► pod :8080
            (Host: order.kiteloomstudio.com)    HTTPRoute
```

Traefik LB'si, Gateway ve HTTPRoute [Gateway API rehberinde](../GatewayAPI/gateway-api.md) anlatılır.

### Neden LoadBalancer değil?

Bu Service önceden `type: LoadBalancer` idi ve kendi Hetzner LB'si vardı. Gateway kurulduktan sonra
ClusterIP'e çevrildi:

| | Service başına LoadBalancer | ClusterIP + Gateway |
|---|---|---|
| Hetzner LB sayısı | Her uygulama için bir LB | Tüm uygulamalar için tek LB (Traefik) |
| Maliyet | Uygulama başına aylık 7,89 € | Toplam aylık 7,89 € |
| Alan adına göre yönlendirme | Yok, her uygulamanın ayrı IP'si olur | Var, HTTPRoute `hostnames` ile |
| Dışarıdan erişim | LB IP'si üzerinden doğrudan | Sadece Gateway üzerinden |

**Not:** `type`'ı `LoadBalancer`'dan `ClusterIP`'e çevirip `kubectl apply` yapınca CCM, Service'in
Hetzner LB'sini kendisi siler. NodePort (`30080`) da kapanır:

```bash
kubectl apply -f kubernetes/week-6/PrivateRegistry/3-service.yaml
kubectl -n week6 get events --field-selector involvedObject.kind=Service
```

```text
Normal   DeletingLoadBalancer   service/week6-api   Deleting load balancer
Normal   DeletedLoadBalancer    service/week6-api   Deleted load balancer
```

---

## 8. Workflow

[`.github/workflows/week6-api.yml`](../../../.github/workflows/week6-api.yml) tek bir job ve 6 adımdan
oluşur. Hazır action olarak sadece `actions/checkout` kullanılır. Diğer adımlar, derste kullandığınız
`docker` ve `kubectl` komutlarıyla aynıdır.

### Ne zaman çalışır?

```yaml
on:
  push:
    branches: [main]
    paths:
      - "kubernetes/week-6/PrivateRegistry/**"
      - "!kubernetes/week-6/PrivateRegistry/**/*.md"
  workflow_dispatch:
```

- `push` + `branches: [main]`: Bir PR main'e merge edildiğinde veya main'e doğrudan push yapıldığında.
- `paths`: Sadece bu klasördeki dosyalar değiştiyse. Repodaki diğer haftaların değişiklikleri deploy tetiklemez.
- `!**/*.md`: Sadece doküman değiştiyse deploy yapılmaz.
- `workflow_dispatch`: Actions sekmesinden **Run workflow** ile elle çalıştırmak için.

Workflow dosyasının kendisi `paths` listesinde yoktur. Sadece workflow'u değiştirirseniz otomatik
çalışmaz, elle çalıştırmanız gerekir:

```bash
gh workflow run week6-api.yml --ref main
```

### Yetkiler ve değişkenler

```yaml
permissions:
  contents: read
  packages: write

env:
  IMAGE: ghcr.io/fcakiroglu16/week6-api
  APP_DIR: kubernetes/week-6/PrivateRegistry
```

- `permissions`: `GITHUB_TOKEN`'ın yetkileri. Kod okunur, GHCR'ye image yazılır. Başka yetki verilmez.
- `env`: Tüm adımlarda kullanılan değişkenler. GHCR image adları küçük harf olmak zorundadır.

### Adımlar

`runs-on: ubuntu-latest`: Job, GitHub'ın verdiği boş bir Ubuntu makinesinde çalışır. Bu makinede
`docker` ve `kubectl` hazır kuruludur. Makine x86 (amd64) olduğu için image, Hetzner node'larıyla aynı
mimaride build edilir.

| Adım | Komut | Ne yapar |
|---|---|---|
| 1. checkout | `uses: actions/checkout@v7` | Repoyu makineye indirir. Makine boş başlar. |
| 2. Image etiketi | `echo "TAG=sha-${GITHUB_SHA::7}" >> "$GITHUB_ENV"` | Commit hash'inin ilk 7 karakterinden `sha-1a2b3c4` etiketi üretir. `$GITHUB_ENV`'e yazılan değişken sonraki adımlarda `$TAG` olarak kullanılır. |
| 3. Docker build ve push | `docker login`, `docker build`, `docker push` | `GITHUB_TOKEN` ile GHCR'ye giriş yapar. Image'ı `sha-1a2b3c4` ve `latest` etiketleriyle build edip gönderir. |
| 4. Cluster'a bağlan | `echo "$KUBE_CONFIG" > ~/.kube/config`, `kubectl get nodes` | Admin kubeconfig'i kubectl'in varsayılan yerine yazar. Node listesi bağlantının çalıştığını gösterir. |
| 5. Image pull secret | `kubectl create secret docker-registry ...` | Cluster'daki `ghcr-pull-secret`'ı oluşturur veya günceller |
| 6. Deploy | `sed`, `kubectl apply`, `kubectl rollout status` | Deployment'taki etiketi değiştirir, yaml'ları uygular, yeni pod'lar hazır olana kadar bekler |

Adımların tam komutları (workflow'daki `run:` blokları):

```bash
# 2. Image etiketi
echo "TAG=sha-${GITHUB_SHA::7}" >> "$GITHUB_ENV"

# 3. Docker build ve push
echo "$GITHUB_TOKEN" | docker login ghcr.io -u "${{ github.actor }}" --password-stdin
docker build --build-arg APP_VERSION=$TAG -t $IMAGE:$TAG -t $IMAGE:latest $APP_DIR/Week6.API
docker push --all-tags $IMAGE

# 4. Cluster'a bağlan
mkdir -p ~/.kube
echo "$KUBE_CONFIG" > ~/.kube/config
kubectl get nodes

# 5. Image pull secret
kubectl create secret docker-registry ghcr-pull-secret \
  --namespace week6 \
  --docker-server=ghcr.io \
  --docker-username=fcakiroglu16 \
  --docker-password="$GHCR_PULL_TOKEN" \
  --dry-run=client -o yaml | kubectl apply -f -

# 6. Deploy
sed -i "s|$IMAGE:latest|$IMAGE:$TAG|" $APP_DIR/2-deployment.yaml
kubectl apply -f $APP_DIR/2-deployment.yaml -f $APP_DIR/3-service.yaml
kubectl rollout status deployment/week6-api -n week6
```

`$IMAGE` ve `$APP_DIR` workflow'un `env:` bloğundan, `$TAG` 2. adımdan, `$GITHUB_TOKEN`,
`$KUBE_CONFIG` ve `$GHCR_PULL_TOKEN` ise adımların `env:` bloklarındaki secret'lardan gelir.

Herhangi bir adım hata verirse sonraki adımlar çalışmaz ve workflow kırmızı (failed) olur. Örneğin
build başarısız olursa deploy yapılmaz, cluster'daki eski sürüm çalışmaya devam eder.

### Secret'lar adımlara nasıl verilir?

```yaml
      - name: Cluster'a bağlan
        env:
          KUBE_CONFIG: ${{ secrets.KUBE_CONFIG }}
        run: |
          echo "$KUBE_CONFIG" > ~/.kube/config
```

`${{ secrets.KUBE_CONFIG }}` doğrudan `run:` komutunun içine yazılmaz. Önce adımın `env:` bloğunda
bir ortam değişkenine atanır, komut bu değişkeni (`$KUBE_CONFIG`) kullanır. Nedeni: `${{ }}` ifadesi
komut çalışmadan önce metin olarak yerine konur. Secret'ın içinde `$` veya `"` gibi karakterler varsa
shell bunları yorumlar ve komut bozulur. Ortam değişkeni ise olduğu gibi aktarılır.

GitHub, secret değerlerini log'larda `***` olarak gizler.

### Build ve push

```bash
echo "$GITHUB_TOKEN" | docker login ghcr.io -u "${{ github.actor }}" --password-stdin
docker build --build-arg APP_VERSION=$TAG -t $IMAGE:$TAG -t $IMAGE:latest $APP_DIR/Week6.API
docker push --all-tags $IMAGE
```

- `--password-stdin`: Token'ı komut satırı argümanı yerine standart girdiden verir. Böylece token işlem
  listesinde görünmez.
- `-t` iki kez: Aynı image'a iki etiket verilir. `sha-1a2b3c4` deploy için kullanılır, `latest` en son
  sürümü gösterir.
- `--all-tags`: Image'ın tüm etiketlerini tek komutla gönderir.

Log'da iki etiketin aynı image'ı (aynı digest) gösterdiği görülür:

```text
Login Succeeded
latest: digest: sha256:881f216b... size: 856
sha-15b32c4: digest: sha256:881f216b... size: 856
```

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
sed -i "s|ghcr.io/fcakiroglu16/week6-api:latest|ghcr.io/fcakiroglu16/week6-api:sha-1a2b3c4|" 2-deployment.yaml
```

Dosya sadece runner'da değişir, repoya geri yazılmaz.

---

## 9. İlk çalıştırma ve doğrulama

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
✓ build-and-deploy in 44s
```

Log'da her adımın çıktısı görünür. Cluster'a bağlan adımı:

```text
NAME                         STATUS   ROLES                AGE   VERSION
egitim-master1               Ready    control-plane,etcd   11h   v1.37.2+k3s1
egitim-pool-worker-worker1   Ready    <none>               11h   v1.37.2+k3s1
...
```

Image pull secret ve Deploy adımları:

```text
secret/ghcr-pull-secret created
deployment.apps/week6-api configured
service/week6-api unchanged
Waiting for deployment "week6-api" rollout to finish: 1 out of 3 new replicas have been updated...
Waiting for deployment "week6-api" rollout to finish: 2 out of 3 new replicas have been updated...
Waiting for deployment "week6-api" rollout to finish: 1 old replicas are pending termination...
deployment "week6-api" successfully rolled out
```

İlk çalıştırmada secret `created` olur. Sonraki çalışmalarda değişmediyse `unchanged` yazar.

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

### Service'i doğrulayın

```bash
kubectl -n week6 get svc week6-api
kubectl -n week6 get endpointslices -l kubernetes.io/service-name=week6-api
```

```text
NAME        TYPE        CLUSTER-IP      EXTERNAL-IP   PORT(S)   AGE
week6-api   ClusterIP   10.43.176.185   <none>        80/TCP    14h

NAME              ADDRESSTYPE   PORTS   ENDPOINTS                             AGE
week6-api-vfjmm   IPv4          8080    10.244.4.15,10.244.3.15,10.244.1.14   14h
```

`EXTERNAL-IP` boştur, çünkü Service dışarıya açık değildir. `ENDPOINTS` 3 pod'un IP'sini gösterir.
Bir pod hazır (`/health` başarılı) değilse burada görünmez ve ona trafik gitmez.

Gateway kurulmadan önce Service'i kendi makinenizden test etmek için `port-forward` kullanın:

```bash
kubectl -n week6 port-forward svc/week6-api 8080:80
```

Başka bir terminalde:

```bash
curl http://localhost:8080/
```

```json
{"message":"Week6.API çalışıyor","version":"sha-15b32c4","pod":"week6-api-85c99847f8-68flh"}
```

### Dışarıdan erişin

Dış erişim için önce [Gateway API rehberini](../GatewayAPI/gateway-api.md) uygulayın. Sonra cluster
dışındaki herhangi bir makineden:

```bash
curl http://order.kiteloomstudio.com/
curl http://order.kiteloomstudio.com/health
```

DNS kaydı henüz yoksa aynı isteği Traefik LB'sinin IP'sine `Host` başlığı ile gönderin:

```bash
curl -H "Host: order.kiteloomstudio.com" http://2.31.140.127/
```

```json
{"message":"Week6.API çalışıyor","version":"sha-15b32c4","pod":"week6-api-85c99847f8-w2f2s"}
```

İsteklerin pod'lara dağıldığını görmek için birkaç kez çağırın:

```bash
for i in $(seq 1 12); do curl -s -H "Host: order.kiteloomstudio.com" http://2.31.140.127/ | jq -r .pod; done | sort | uniq -c
```

```text
   5 week6-api-85c99847f8-68flh
   3 week6-api-85c99847f8-7vshm
   4 week6-api-85c99847f8-w2f2s
```

`version` alanı deploy edilen commit'i gösterir.

---

## 10. Paketi private yapın

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

## 11. Pull secret'ın etkisini test edin

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

## 12. Yeni sürüm yayınlama

Yeni sürüm için ayrı bir işlem gerekmez. API kodunda veya yaml'larda yapılan değişiklik main'e merge
edildiğinde workflow çalışır, yeni commit etiketiyle image üretir ve deployment'ı günceller.

### 12.1 Değişikliği main'e gönderin

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
değişmediği için rollout başlamaz. Yeni pod'lar istiyorsanız:

```bash
kubectl -n week6 rollout restart deployment/week6-api
```

### 12.2 Ne oldu?

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
while true; do curl -s -H "Host: order.kiteloomstudio.com" http://2.31.140.127/ | jq -r .version; sleep 0.5; done
```

```text
sha-45c0c53
sha-45c0c53
sha-605a99a
sha-45c0c53
sha-605a99a
sha-605a99a
```

### 12.3 Kesintisiz geçiş: preStop

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
| Pod `ImagePullBackOff`, mesaj `not found` | Pull secret yok, yanlış veya token süresi dolmuş | `kubectl -n week6 get secret ghcr-pull-secret`, PAT'in süresi: https://github.com/settings/tokens |
| Pod `ImagePullBackOff`, mesaj `exec format error` veya `no match for platform` | Image arm64 build edilmiş | Build'de `--platform linux/amd64` |
| Workflow `Cluster'a bağlan` adımında zaman aşımı | 6443 portu GitHub'a kapalı | `hcloud firewall describe egitim` |
| Workflow `Unauthorized` veya sertifika hatası | Cluster yeniden kuruldu, `KUBE_CONFIG` eski | `KUBE_CONFIG` secret'ını yeni `kubeconfig` ile güncelleyin |
| Workflow `GHCR_PULL_TOKEN secret'ı tanımlı değil` | Secret eklenmemiş | `gh secret list` |
| Build adımında `denied: permission_denied` | Workflow'da `packages: write` yok | `permissions` bloğu |
| `rollout status` zaman aşımı | Pod `/health` probe'unu geçemiyor | `kubectl -n week6 describe pods -l app=week6-api` |
| Gateway'den `404` geliyor | HTTPRoute bağlanmamış veya `Host` başlığı yanlış | `kubectl -n week6 describe httproute week6-api`, bkz. [Gateway API sorun giderme](../GatewayAPI/gateway-api.md#sorun-giderme) |
| Gateway'den `503` geliyor | Service'in hazır pod'u yok | `kubectl -n week6 get endpointslices -l kubernetes.io/service-name=week6-api` |

## Temizlik

Uygulamayı silmek için (admin kubeconfig ile):

```bash
kubectl delete namespace week6
```

Namespace silinince içindeki deployment, service ve pull secret da silinir.

**Dikkat:** GitHub'daki `KUBE_CONFIG` secret'ı geçerli kalır, çünkü admin kimliği namespace'e bağlı
değildir. Cluster duruyorsa bu secret'ı aşağıdaki gibi mutlaka silin.

Bu Service `ClusterIP` olduğu için Hetzner'de ona ait bir LB yoktur. Traefik LB'sinin temizliği
[Gateway API rehberinde](../GatewayAPI/gateway-api.md#temizlik) anlatılır.

GitHub tarafı:

```bash
gh secret delete KUBE_CONFIG
gh secret delete GHCR_PULL_TOKEN
```

PAT'i https://github.com/settings/tokens sayfasından silin. GHCR paketini Package settings > Danger Zone >
Delete this package ile silebilirsiniz.

API erişimini tekrar kendi IP'nize kısıtlamak için `hetzner-k3s/cluster.yaml` içindeki
`allowed_networks.api` değerini geri alıp `create` komutunu çalıştırın.
