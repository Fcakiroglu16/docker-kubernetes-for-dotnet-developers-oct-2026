# Secret

Secret, şifre, token, API anahtarı, sertifika gibi **hassas bilgileri** tutan Kubernetes nesnesidir. Kullanımı ConfigMap'e çok benzer (`anahtar=değer` çiftleri, aynı kullanma yolları). Farkları:

| | ConfigMap | Secret |
|---|---|---|
| Ne için | Gizli olmayan ayarlar | Şifre, token, anahtar, sertifika |
| Değerler `data` altında | Düz metin | base64 ile kodlanmış |
| `kubectl describe` | Değerleri gösterir | Değerleri gizler, sadece boyutunu gösterir |
| Türü (`type`) | Yok | Var (Opaque, tls, ...) |

> **base64 şifreleme değildir.** Secret'taki değeri `base64 -d` ile herkes geri çevirebilir. Secret'ı korumak için RBAC ile erişimi kısıtla ve gerekiyorsa cluster'da etcd şifrelemesini aç. Bu dokümandaki tüm değerler sahte demo değerleridir. Gerçek şifreleri asla repoya koyma.

Bu dokümanda Secret'ı **oluşturmanın 4 yolu**, bir Pod'da **kullanmanın 3 yolu** ve **Secret türleri** çalışan örneklerle anlatılır. Önce `Microservice1.API` image'ının (`microservice1-api:1.6`) build edilmiş olması gerekir (bkz. [pod.md](../pod/pod.md)). Tüm komutlar repo kök klasöründen çalıştırılır.

## Secret türleri

Her Secret'ın bir türü (`type`) vardır. Tür, Secret'ın ne tür veri tuttuğunu söyler. Bazı türlerde API sunucusu gerekli anahtarların varlığını da kontrol eder.

| Tür | Açıklama |
|---|---|
| `Opaque` | Varsayılan tür. Herhangi bir anahtar=değer verisi tutar |
| `kubernetes.io/tls` | TLS sertifikası ve özel anahtarı. `tls.crt` ve `tls.key` anahtarları zorunludur |
| `kubernetes.io/dockerconfigjson` | Özel (private) container registry kimlik bilgileri |
| `kubernetes.io/basic-auth` | Kullanıcı adı ve şifre |
| `kubernetes.io/ssh-auth` | SSH özel anahtarı |

Aşağıdaki ilk bölümler `Opaque` türünü kullanır. Diğer türlerin örnekleri en sonda ("Diğer Secret türleri") anlatılır.

## Secret oluşturmanın 4 yolu

### Yol 1: Komutla, değerleri yazarak (`--from-literal`)

`kubectl create secret generic` komutu `Opaque` türünde Secret oluşturur. Değerde özel karakter (`!`, `$`) varsa tek tırnak kullan:

```bash
kubectl create secret generic db-literal -n ecommerce-dev \
  --from-literal=DB_USER=appuser \
  --from-literal=DB_PASSWORD='Demo-Passw0rd!'
```

### Yol 2: Dosyadan (`--from-file`)

Dosyanın adı anahtar, içeriği değer olur. Dosya: [appsettings.Secrets.json](appsettings.Secrets.json)

```bash
kubectl create secret generic db-file -n ecommerce-dev \
  --from-file=kubernetes/week-3/Secret/appsettings.Secrets.json
```

Anahtarın adını dosya adından farklı vermek için `--from-file=anahtar=dosya-yolu` biçimi kullanılır.

### Yol 3: `.env` dosyasından (`--from-env-file`)

Dosyadaki her `ANAHTAR=DEĞER` satırı ayrı bir anahtar olur. `--from-file` dosyanın tamamını tek bir değer yapar, `--from-env-file` ise satır satır anahtarlara böler. `#` ile başlayan satırlar yorum sayılır. Dosya: [db.env](db.env)

```bash
kubectl create secret generic db-envfile -n ecommerce-dev \
  --from-env-file=kubernetes/week-3/Secret/db.env
```

### Yol 4: YAML ile (`kubectl apply`)

Üç ayrı örnek vardır.

**a) Düz metin yazmak (`stringData`).** Değerleri düz metin yazarsın, Kubernetes kaydederken base64'e çevirir. Dosya: [secret.yaml](secret.yaml)

```bash
kubectl apply -f kubernetes/week-3/Secret/secret.yaml
```

**b) base64 ile kodlanmış yazmak (`data`).** Değerleri kendin kodlayıp yazarsın. Kodlarken `echo -n` kullan, `-n` olmazsa satır sonu karakteri de değere dahil olur. Dosya: [secret-base64.yaml](secret-base64.yaml)

```bash
echo -n 'appuser' | base64
kubectl apply -f kubernetes/week-3/Secret/secret-base64.yaml
```

> `stringData` yalnızca yazma içindir. Secret'ı sonradan okuduğunda `stringData` görünmez, her şey `data` altında base64 olarak görünür.

**c) JSON'u doğrudan YAML içine yazmak.** Anahtar bir dosya adıdır (`appsettings.Secrets.json`), değer ise o dosyanın içeriğidir. JSON, `|` işaretinden sonra olduğu gibi YAML içine yazılır. Dosya: [secret-json.yaml](secret-json.yaml)

```bash
kubectl apply -f kubernetes/week-3/Secret/secret-json.yaml
```

> Kubernetes içeriği doğrulamaz, sadece string olarak saklar. Geçerli JSON olması senin sorumluluğundadır.

## Secret'ları göster

`TYPE` sütunu Secret türünü, `DATA` sütunu anahtar sayısını gösterir:

```bash
kubectl get secrets -n ecommerce-dev
```

`describe` değerleri göstermez, sadece her anahtarın boyutunu (bayt) gösterir:

```bash
kubectl describe secret db-literal -n ecommerce-dev
```

`-o yaml` değerleri base64 olarak gösterir:

```bash
kubectl get secret db-literal -n ecommerce-dev -o yaml
```

Tek bir anahtarın değerini al ve base64'ten çöz:

```bash
kubectl get secret db-literal -n ecommerce-dev -o jsonpath='{.data.DB_PASSWORD}' | base64 -d
```

Anahtarda `.` varsa `\.` ile kaçırılmalıdır. YAML içine yazdığımız JSON'u çöz ve geçerli JSON olup olmadığını kontrol et:

```bash
kubectl get secret app-secret-json -n ecommerce-dev -o jsonpath='{.data.appsettings\.Secrets\.json}' | base64 -d | python3 -m json.tool
```

## Secret'ı Pod'da kullanmanın 3 yolu

### Yol 1: Tek tek anahtar seçip ortam değişkeni yap (`env` + `secretKeyRef`)

`db-literal` Secret'ından iki anahtar seçilir. `DB_PASSWORD` anahtarı container içinde `DATABASE_PASSWORD` adıyla ortam değişkeni olur. Dosya: [pod-env.yaml](pod-env.yaml)

```bash
kubectl apply -f kubernetes/week-3/Secret/pod-env.yaml
kubectl wait --for=condition=Ready pod/sec-env -n ecommerce-dev --timeout=60s
```

Ortam değişkenlerini kontrol et. `appuser` ve `Demo-Passw0rd!` yazmalı:

```bash
kubectl exec sec-env -n ecommerce-dev -- printenv DB_USER DATABASE_PASSWORD
```

`kubectl describe pod` Secret değerini göstermez, sadece nereden geldiğini yazar:

```bash
kubectl describe pod sec-env -n ecommerce-dev | grep -A3 'DB_USER:'
```

### Yol 2: Tüm anahtarları ortam değişkeni yap (`envFrom`)

`db-yaml` Secret'ındaki tüm anahtarlar (`DB_USER`, `DB_PASSWORD`) ortam değişkeni olur. Dosya: [pod-envfrom.yaml](pod-envfrom.yaml)

```bash
kubectl apply -f kubernetes/week-3/Secret/pod-envfrom.yaml
kubectl wait --for=condition=Ready pod/sec-envfrom -n ecommerce-dev --timeout=60s
```

`appuser` ve `Demo-Yaml-Passw0rd` yazmalı:

```bash
kubectl exec sec-envfrom -n ecommerce-dev -- printenv DB_USER DB_PASSWORD
```

### Yol 3: Dosya olarak bağla (`volume` + `volumeMounts`)

Üç bağlama gösterilir. Dosya: [pod-volume.yaml](pod-volume.yaml)

- `db-file` Secret'ındaki `appsettings.Secrets.json` anahtarı, `subPath` ile container içinde tek bir dosya olarak (`/app/appsettings.Secrets.json`) bağlanır.
- `db-yaml` Secret'ı `/etc/app-secrets` klasörü olarak bağlanır. Her anahtar bir dosya olur.
- `app-secret-json` Secret'ı `/etc/app-secret-json` klasörü olarak bağlanır. YAML içine yazdığımız JSON, `appsettings.Secrets.json` dosyası olur.

> ConfigMap volume'unda `configMap.name` yazılırken Secret volume'unda `secret.secretName` yazılır.

```bash
kubectl apply -f kubernetes/week-3/Secret/pod-volume.yaml
kubectl wait --for=condition=Ready pod/sec-volume -n ecommerce-dev --timeout=60s
```

Dosyaları container içinde göster:

```bash
kubectl exec sec-volume -n ecommerce-dev -- cat /app/appsettings.Secrets.json
kubectl exec sec-volume -n ecommerce-dev -- ls /etc/app-secrets
kubectl exec sec-volume -n ecommerce-dev -- cat /etc/app-secrets/DB_PASSWORD
kubectl exec sec-volume -n ecommerce-dev -- cat /etc/app-secret-json/appsettings.Secrets.json
```

Secret volume'ları diske değil, belleğe (`tmpfs`) yazılır. Böylece Secret node'un diskine düşmez:

```bash
kubectl exec sec-volume -n ecommerce-dev -- df /etc/app-secrets
```

## Secret değişince ne olur

Secret'ın değerini değiştir. `stringData` ile düz metin yazabilirsin:

```bash
kubectl patch secret db-yaml -n ecommerce-dev --type merge -p '{"stringData":{"DB_PASSWORD":"Demo-Yeni-Passw0rd"}}'
```

Kullanım yoluna göre sonuç ConfigMap ile aynıdır:

| Kullanım | Secret değişince |
|---|---|
| Ortam değişkeni (`env`, `envFrom`) | Güncellenmez. Pod yeniden oluşturulmalıdır |
| Volume, klasör olarak bağlı | Kubelet bir süre sonra (genelde 1 dakika içinde) dosyayı günceller |
| Volume, `subPath` ile bağlı | Güncellenmez. Pod yeniden oluşturulmalıdır |

Aşağıdaki komutları 1-2 dakika sonra çalıştır. Ortam değişkeni eski değerde kalır, klasör olarak bağlı dosya yeni değeri gösterir:

```bash
kubectl exec sec-envfrom -n ecommerce-dev -- printenv DB_PASSWORD
kubectl exec sec-volume -n ecommerce-dev -- cat /etc/app-secrets/DB_PASSWORD
```

## Diğer Secret türleri

Tür, `kubectl create secret <tür>` alt komutuyla ya da `--type` parametresiyle belirlenir. Örneklerde kullanılacak geçici sertifika ve anahtar dosyaları `/tmp/secret-demo` klasörüne yazılır, repoya girmez.

```bash
mkdir -p /tmp/secret-demo
```

### `kubernetes.io/tls`: TLS sertifikası

Önce deneme için kendinden imzalı (self-signed) bir sertifika üret:

```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
  -subj "/CN=ecommerce.local" \
  -keyout /tmp/secret-demo/tls.key -out /tmp/secret-demo/tls.crt
```

`kubectl create secret tls` komutu `tls.crt` ve `tls.key` anahtarlarını kendisi oluşturur:

```bash
kubectl create secret tls tls-demo -n ecommerce-dev \
  --cert=/tmp/secret-demo/tls.crt --key=/tmp/secret-demo/tls.key
```

Anahtar adları zorunludur. Eksikse API sunucusu Secret'ı reddeder:

```bash
kubectl create secret generic tls-bad -n ecommerce-dev \
  --type=kubernetes.io/tls --from-literal=foo=bar
# Error: ... data[tls.crt]: Required value ... data[tls.key]: Required value
```

### `kubernetes.io/dockerconfigjson`: Özel registry kimlik bilgisi

`docker-registry` alt komutu doğru formatta `.dockerconfigjson` anahtarını oluşturur:

```bash
kubectl create secret docker-registry regcred -n ecommerce-dev \
  --docker-server=registry.example.com \
  --docker-username=demo \
  --docker-password='Demo-Registry-Passw0rd' \
  --docker-email=demo@example.com
```

İçeriği çöz:

```bash
kubectl get secret regcred -n ecommerce-dev -o jsonpath='{.data.\.dockerconfigjson}' | base64 -d | python3 -m json.tool
```

Pod'a `imagePullSecrets` ile verilir. Dosya: [pod-imagepull.yaml](pod-imagepull.yaml)

```bash
kubectl apply -f kubernetes/week-3/Secret/pod-imagepull.yaml
kubectl get pod sec-imagepull -n ecommerce-dev -o jsonpath='{.spec.imagePullSecrets}'
```

> Örnekte image yerelde olduğu için registry'ye hiç gidilmez ve kimlik bilgisi kullanılmaz. Gerçek bir özel registry'den çekme bu dokümanda denenmemiştir.

#### Örnek: GitHub Container Registry (ghcr.io)

GitHub'a yüklediğin özel (private) image'ı çekmek için kimlik bilgisi gerekir. Kullanıcı adın ve bir **Personal Access Token** (GitHub → Settings → Developer settings → Personal access tokens) kullanılır. Token'a image okumak için `read:packages` yetkisi verilmelidir. Public image'lar için Secret gerekmez.

> Aşağıdaki komutlarda `demo-user` ve `ghp_DEMO_TOKEN_REPLACE_ME` sahte değerlerdir. Kendi kullanıcı adın ve token'ınla değiştir. Gerçek token'ı asla repoya koyma.

**a) Komutla oluştur.** `--docker-server=ghcr.io` verilir:

```bash
kubectl create secret docker-registry ghcr-cmd -n ecommerce-dev \
  --docker-server=ghcr.io \
  --docker-username=demo-user \
  --docker-password='ghp_DEMO_TOKEN_REPLACE_ME'
```

**b) YAML ile oluştur.** Tür `kubernetes.io/dockerconfigjson`, anahtar adı tam olarak `.dockerconfigjson` olmalıdır. `auth` alanı `kullanıcı:token` metninin base64'üdür. Dosya: [secret-ghcr.yaml](secret-ghcr.yaml)

```bash
echo -n 'demo-user:ghp_DEMO_TOKEN_REPLACE_ME' | base64
kubectl apply -f kubernetes/week-3/Secret/secret-ghcr.yaml
```

İki yöntem aynı içeriği üretir. İçeriği çözerek kontrol et:

```bash
kubectl get secret ghcr-secret -n ecommerce-dev -o jsonpath='{.data.\.dockerconfigjson}' | base64 -d | python3 -m json.tool
```

Pod'da `imagePullSecrets` ile kullanılır. Dosya: [pod-ghcr.yaml](pod-ghcr.yaml). Dosyadaki `<github-kullanici-adi>` yerini kendi kullanıcı adınla doldurmalısın. Aşağıdaki komut denemek için `demo-user` yazar:

```bash
sed 's#<github-kullanici-adi>#demo-user#' kubernetes/week-3/Secret/pod-ghcr.yaml | kubectl apply -f -
kubectl get pod sec-ghcr -n ecommerce-dev
```

Sahte kullanıcı ve olmayan image ile Pod, birkaç saniye `ContainerCreating` kaldıktan sonra `ImagePullBackOff` olur. Bu beklenen sonuçtur. Hata mesajında `failed to authorize: failed to fetch oauth token ... https://ghcr.io/token` yazar. Bu, kubelet'in Secret'taki kimlik bilgisiyle `ghcr.io`'ya gittiğini ama reddedildiğini gösterir:

```bash
kubectl describe pod sec-ghcr -n ecommerce-dev | grep 'Failed to pull'
```

> Gerçek token ve yayımlanmış bir özel image ile çekmenin başarılı olması bu dokümanda denenmemiştir.

Image'ı `ghcr.io`'ya yüklemek için (bu dokümanda denenmemiştir; token'a `write:packages` yetkisi gerekir):

```bash
echo "$GITHUB_TOKEN" | docker login ghcr.io -u <github-kullanici-adi> --password-stdin
docker tag microservice1-api:1.6 ghcr.io/<github-kullanici-adi>/microservice1-api:1.6
docker push ghcr.io/<github-kullanici-adi>/microservice1-api:1.6
```

### `kubernetes.io/basic-auth`: Kullanıcı adı ve şifre

`username` ve `password` anahtarlarından en az biri zorunludur:

```bash
kubectl create secret generic basic-demo -n ecommerce-dev \
  --type=kubernetes.io/basic-auth \
  --from-literal=username=admin \
  --from-literal=password='Demo-Basic-Passw0rd'
```

### `kubernetes.io/ssh-auth`: SSH özel anahtarı

`ssh-privatekey` anahtarı zorunludur. Önce deneme için bir anahtar çifti üret:

```bash
ssh-keygen -t ed25519 -N "" -C "demo" -f /tmp/secret-demo/id_demo
kubectl create secret generic ssh-demo -n ecommerce-dev \
  --type=kubernetes.io/ssh-auth \
  --from-file=ssh-privatekey=/tmp/secret-demo/id_demo
```

### Türleri karşılaştır

```bash
kubectl get secrets -n ecommerce-dev
```

## Notlar

- Bir Secret'ın boyutu en fazla 1 MiB olabilir.
- Pod'un kullandığı Secret Pod'dan önce oluşturulmalıdır. Yoksa Pod `CreateContainerConfigError` (env) ya da `ContainerCreating` (volume) durumunda bekler.
- `ecommerce-dev` namespace'inde ResourceQuota ile Secret sayısı sınırlıdır (bkz. [namespace.md](../namespace/namespace.md)).
- Bir Secret `immutable: true` ile değiştirilemez hâle getirilebilir.
- Ortam değişkeni olarak verilen Secret, uygulama çökerken alınan döküm (dump) ve log'lara kolay sızabilir. Mümkünse volume ile dosya olarak vermek daha güvenlidir.

## Temizle

```bash
kubectl delete pod sec-env sec-envfrom sec-volume sec-imagepull sec-ghcr -n ecommerce-dev
kubectl delete secret db-literal db-file db-envfile db-yaml db-base64 app-secret-json tls-demo regcred ghcr-cmd ghcr-secret basic-demo ssh-demo -n ecommerce-dev
rm -rf /tmp/secret-demo
```
