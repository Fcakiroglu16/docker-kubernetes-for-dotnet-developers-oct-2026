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
           Kullanıcı ── http://<lb-ip>/ ──►│ Hetzner Load Balancer :80│
                                           └──────────────────────────┘
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

## 7. Service (Hetzner Load Balancer)

[`3-service.yaml`](3-service.yaml)

```yaml
metadata:
  annotations:
    load-balancer.hetzner.cloud/name: week6-api
    load-balancer.hetzner.cloud/location: fsn1
    load-balancer.hetzner.cloud/type: lb11
    load-balancer.hetzner.cloud/use-private-ip: "true"
spec:
  type: LoadBalancer
  selector:
    app: week6-api
  ports:
    - port: 80
      targetPort: 8080
```

`type: LoadBalancer` olan bir Service oluşunca **Hetzner Cloud Controller Manager (CCM)** Hetzner'de
bir Load Balancer açar ve public IP'sini Service'in `EXTERNAL-IP` alanına yazar. Kullanıcılar
uygulamaya tek ve sabit bir IP'den, 80 portundan ulaşır.

### NodePort yerine neden LoadBalancer?

| | NodePort | LoadBalancer |
|---|---|---|
| Adres | Herhangi bir node'un IP'si + `30080` | Tek bir LB IP'si + `80` |
| Node kapanırsa | O node'un IP'sini kullanan istemciler erişemez | LB sağlıksız node'a trafik göndermez |
| Node eklenip çıkınca | İstemciler yeni IP'leri bilmeli | CCM LB hedeflerini kendisi günceller |
| DNS | Bir node IP'sine bağlanır | LB IP'sine bağlanır, IP değişmez |
| Maliyet | Ücretsiz | `lb11` aylık 7,89 € |

### Annotation'lar

| Annotation | Ne yapar |
|---|---|
| `load-balancer.hetzner.cloud/location` | **Zorunlu.** LB'nin açılacağı lokasyon. Node'larla aynı olmalı. Yazılmazsa LB oluşmaz. |
| `load-balancer.hetzner.cloud/type` | LB tipi. `lb11` en küçüğüdür: en fazla 25 hedef (node), 5 servis (port), 10.000 eş zamanlı bağlantı. Aylık 7,89 €. |
| `load-balancer.hetzner.cloud/name` | Hetzner Console'daki LB adı. Yazılmazsa rastgele bir ad verilir. |
| `load-balancer.hetzner.cloud/use-private-ip` | LB node'lara private network (`10.0.x.x`) üzerinden bağlanır. Trafik internete çıkmaz ve ücretsiz kotadan düşmez. |

### Trafik nasıl akar?

```text
İstemci ──► LB 2.31.101.239:80 ──► node 10.0.0.x:30080 (NodePort) ──► kube-proxy ──► pod :8080
```

1. LB isteği round-robin ile bir node'a, private network üzerinden gönderir.
2. Node'daki NodePort isteği alır. LoadBalancer Service arka planda bir NodePort da açar.
3. kube-proxy isteği `app: week6-api` label'lı pod'lardan birine iletir. Pod başka bir node'da olabilir.

LB her node'u sağlık kontrolü ile izler (NodePort'a TCP bağlantısı). Cevap vermeyen node'a trafik
göndermez.

**Not:** CCM tüm node'ları LB'ye hedef olarak ekler, master da dahil. Master'da uygulama pod'u yoktur,
ama gelen isteği kube-proxy ile worker'daki pod'a iletir. Master'ı hedeflerden çıkarmak için master
node'a `node.kubernetes.io/exclude-from-external-load-balancers` label'ını ekleyebilirsiniz.

**Not:** LoadBalancer Service'in arka planda açtığı NodePort (`30080`) hâlâ node'ların public IP'leri
üzerinden de erişilebilir (`curl http://<node-public-ip>:30080/` → `200`). Çünkü hetzner-k3s firewall'u
30000-32767 aralığını internete açık tutar. Trafiği sadece LB üzerinden kabul etmek için
`hetzner-k3s/cluster.yaml` içinde `networking.node_port_firewall_enabled: false` yapın. LB node'lara
`use-private-ip` ile private network'ten bağlandığı ve Hetzner firewall'u private network trafiğine
uygulanmadığı için LB'nin etkilenmemesi gerekir. Bu ayar bu rehberde test edilmedi.

**Dikkat:** `hetzner-k3s delete` bu LB'yi silmez. Cluster'ı silmeden önce Service'i silin. Bkz. [Temizlik](#temizlik).

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

### Load Balancer'ı doğrulayın

CCM'in LB'yi oluşturması birkaç saniye sürer. `EXTERNAL-IP` dolana kadar bekleyin:

```bash
kubectl -n week6 get svc week6-api -w
```

```text
NAME        TYPE           CLUSTER-IP      EXTERNAL-IP                                   PORT(S)
week6-api   LoadBalancer   10.43.176.185   10.0.0.6,2.31.101.239,2a01:4f8:c015:e919::1   80:30080/TCP
```

`EXTERNAL-IP` üç adres içerir:

| Adres | Ne |
|---|---|
| `10.0.0.6` | LB'nin private network IP'si. Node'lara bu IP'den bağlanır. |
| `2.31.101.239` | LB'nin public IPv4 adresi. **Kullanıcılar bu adresi kullanır.** |
| `2a01:4f8:c015:e919::1` | LB'nin public IPv6 adresi |

CCM'in olayları:

```bash
kubectl -n week6 get events --field-selector involvedObject.kind=Service
```

```text
EnsuringLoadBalancer   Ensuring load balancer
EnsuredLoadBalancer    Ensured load balancer
```

Hetzner tarafında LB ve hedeflerin sağlık durumu:

```bash
hcloud load-balancer list
hcloud load-balancer describe week6-api
```

```text
NAME        HEALTH    IPV4           TYPE   LOCATION
week6-api   healthy   2.31.101.239   lb11   fsn1
```

`describe` çıktısında 4 node da `private` IP ile ve `healthy` olarak görünür. LB ilk açıldığında
health birkaç saniye `mixed` görünebilir. Sağlık kontrolleri tamamlanınca `healthy` olur.

### Dışarıdan erişin

Cluster dışındaki herhangi bir makineden:

```bash
curl http://2.31.101.239/
```

```json
{"message":"Week6.API çalışıyor","version":"sha-ae05b19","pod":"week6-api-7878c67965-9htlw"}
```

```bash
curl http://2.31.101.239/health
```

```text
Healthy
```

İsteklerin pod'lara dağıldığını görmek için birkaç kez çağırın:

```bash
for i in $(seq 1 12); do curl -s http://2.31.101.239/ | jq -r .pod; done | sort | uniq -c
```

```text
   6 week6-api-7878c67965-9htlw
   1 week6-api-7878c67965-ht9fp
   5 week6-api-7878c67965-mkpxs
```

`version` alanı deploy edilen commit'i gösterir. Dağılım tam eşit değildir, çünkü dağıtım iki
aşamalıdır: LB node seçer, kube-proxy pod seçer.

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
değişmediği için rollout başlamaz. Yeni pod'lar istiyorsanız `kubectl rollout restart` kullanın.

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
while true; do curl -s http://<lb-ip>/ | jq -r .version; sleep 0.5; done
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
| Pod `ImagePullBackOff`, mesaj `not found` | Pull secret yok, yanlış veya token süresi dolmuş | `kubectl -n week6 get secret ghcr-pull-secret`, PAT'in süresi |
| Pod `ImagePullBackOff`, mesaj `exec format error` veya `no match for platform` | Image arm64 build edilmiş | Build'de `--platform linux/amd64` |
| Workflow `Cluster'a bağlan` adımında zaman aşımı | 6443 portu GitHub'a kapalı | `hcloud firewall describe egitim` |
| Workflow `Unauthorized` veya sertifika hatası | Cluster yeniden kuruldu, `KUBE_CONFIG` eski | `KUBE_CONFIG` secret'ını yeni `kubeconfig` ile güncelleyin |
| Workflow `GHCR_PULL_TOKEN secret'ı tanımlı değil` | Secret eklenmemiş | `gh secret list` |
| Build adımında `denied: permission_denied` | Workflow'da `packages: write` yok | `permissions` bloğu |
| `rollout status` zaman aşımı | Pod `/health` probe'unu geçemiyor | `kubectl -n week6 describe pod <pod>` |
| `EXTERNAL-IP` sürekli `<pending>` | `location` annotation'ı yok veya CCM hata veriyor | `kubectl -n week6 describe svc week6-api` olayları, `kubectl -n kube-system logs deploy/hcloud-cloud-controller-manager` |
| LB health `unhealthy` | Node'lara NodePort üzerinden ulaşılamıyor | `hcloud load-balancer describe week6-api`, pod'lar `Running` mi |

## Temizlik

Uygulamayı silmek için (admin kubeconfig ile):

```bash
kubectl delete namespace week6
```

Namespace silinince içindeki deployment, service ve pull secret da silinir.

**Dikkat:** GitHub'daki `KUBE_CONFIG` secret'ı geçerli kalır, çünkü admin kimliği namespace'e bağlı
değildir. Cluster duruyorsa bu secret'ı aşağıdaki gibi mutlaka silin.

Service silinince CCM Hetzner Load Balancer'ı da siler. Kontrol edin, listede `week6-api` kalmamalı:

```bash
hcloud load-balancer list
```

**Bu sırayı koruyun:** Önce namespace'i (veya Service'i), sonra cluster'ı silin. Cluster önce silinirse
LB'yi silecek CCM kalmaz ve LB ücretlendirilmeye devam eder. Bu durumda
`hcloud load-balancer delete week6-api` ile elle silin.

GitHub tarafı:

```bash
gh secret delete KUBE_CONFIG
gh secret delete GHCR_PULL_TOKEN
```

PAT'i https://github.com/settings/tokens sayfasından silin. GHCR paketini Package settings > Danger Zone >
Delete this package ile silebilirsiniz.

API erişimini tekrar kendi IP'nize kısıtlamak için `hetzner-k3s/cluster.yaml` içindeki
`allowed_networks.api` değerini geri alıp `create` komutunu çalıştırın.
