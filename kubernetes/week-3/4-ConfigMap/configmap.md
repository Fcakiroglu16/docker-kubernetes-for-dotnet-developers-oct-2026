# ConfigMap

ConfigMap, uygulama yapılandırmasını (ayarlar, mod, dosya içeriği) image'ın dışında, `anahtar=değer` çiftleri olarak tutan Kubernetes nesnesidir. Böylece aynı image farklı ortamlarda (development, staging, production) farklı ayarlarla çalışabilir. Gizli bilgiler (şifre, token) için ConfigMap değil Secret kullanılır.

Bu dokümanda ConfigMap'i **oluşturmanın 4 yolu** ve bir Pod'da **kullanmanın 3 yolu** çalışan örneklerle anlatılır. Değerlerin container'a ulaştığı `printenv` ve `cat` ile gösterilir.

Önce `Microservice1.API` image'ının (`microservice1-api:1.6`) build edilmiş olması gerekir (bkz. [pod.md](../pod/pod.md)). Tüm komutlar repo kök klasöründen çalıştırılır.

## ConfigMap oluşturmanın 4 yolu

### Yol 1: Komutla, değerleri yazarak (`--from-literal`)

Her `--from-literal` bir anahtar=değer girdisi ekler:

```bash
kubectl create configmap app-literal -n ecommerce-dev \
  --from-literal=APP_COLOR=blue \
  --from-literal=APP_SIZE=large
```

### Yol 2: Dosyadan (`--from-file`)

Dosyanın adı anahtar, içeriği değer olur. Dosya: [appsettings.Production.json](appsettings.Production.json)

```bash
kubectl create configmap app-file -n ecommerce-dev \
  --from-file=kubernetes/week-3/ConfigMap/appsettings.Production.json
```

Anahtarın adını dosya adından farklı vermek için `--from-file=anahtar=dosya-yolu` biçimi kullanılır. Klasör verilirse içindeki her dosya bir anahtar olur.

### Yol 3: `.env` dosyasından (`--from-env-file`)

Dosyadaki her `ANAHTAR=DEĞER` satırı ayrı bir anahtar olur. `--from-file`'dan farkı: `--from-file` dosyanın tamamını tek bir değer yapar, `--from-env-file` dosyayı satır satır anahtarlara böler. `#` ile başlayan satırlar yorum sayılır. Dosya: [app.env](app.env)

```bash
kubectl create configmap app-envfile -n ecommerce-dev \
  --from-env-file=kubernetes/week-3/ConfigMap/app.env
```

### Yol 4: YAML ile (`kubectl apply`)

Değerler `data` altında yazılır. Değerler her zaman string'dir. İki ayrı örnek vardır.

**a) Klasik `anahtar: değer` biçimi.** Dosya: [configmap.yaml](configmap.yaml)

```bash
kubectl apply -f kubernetes/week-3/ConfigMap/configmap.yaml
```

**b) JSON'u doğrudan YAML içine yazmak.** Anahtar bir dosya adıdır (`appsettings.Development.json`), değer ise o dosyanın içeriğidir. JSON, `|` işaretinden sonra olduğu gibi YAML içine yazılır. Ayrı bir `.json` dosyası ve `--from-file` gerekmez. Dosya: [configmap-json.yaml](configmap-json.yaml)

```bash
kubectl apply -f kubernetes/week-3/ConfigMap/configmap-json.yaml
```

> Kubernetes içeriği doğrulamaz, sadece string olarak saklar. Geçerli JSON olması senin sorumluluğundadır.

## ConfigMap'leri göster

```bash
kubectl get configmaps -n ecommerce-dev
```

`DATA` sütunu her ConfigMap'teki anahtar sayısını gösterir. `kube-root-ca.crt` Kubernetes'in kendiliğinden oluşturduğu ConfigMap'tir.

İçeriğini göster:

```bash
kubectl describe configmap app-literal -n ecommerce-dev
kubectl get configmap app-file -n ecommerce-dev -o yaml
```

Tek bir anahtarın değerini al. Anahtardaki `.` karakteri `\.` ile kaçırılmalıdır:

```bash
kubectl get configmap app-file -n ecommerce-dev -o jsonpath='{.data.appsettings\.Production\.json}'
```

YAML içine yazdığımız JSON'u al ve geçerli JSON olup olmadığını kontrol et:

```bash
kubectl get configmap app-json -n ecommerce-dev -o jsonpath='{.data.appsettings\.Development\.json}' | python3 -m json.tool
```

## ConfigMap'i Pod'da kullanmanın 3 yolu

### Yol 1: Tek tek anahtar seçip ortam değişkeni yap (`env` + `configMapKeyRef`)

`app-literal` ConfigMap'inden iki anahtar seçilir. `APP_SIZE` aynı adla, `APP_COLOR` ise `APP_THEME` adıyla ortam değişkeni olur. Dosya: [pod-env.yaml](pod-env.yaml)

```bash
kubectl apply -f kubernetes/week-3/ConfigMap/pod-env.yaml
kubectl wait --for=condition=Ready pod/cm-env -n ecommerce-dev --timeout=60s
```

Ortam değişkenlerini kontrol et. `large` ve `blue` yazmalı:

```bash
kubectl exec cm-env -n ecommerce-dev -- printenv APP_SIZE APP_THEME
```

### Yol 2: Tüm anahtarları ortam değişkeni yap (`envFrom`)

`app-yaml` ConfigMap'indeki tüm anahtarlar (`APP_MODE`, `LOG_LEVEL`) ortam değişkeni olur. Anahtar tek tek yazılmaz. Dosya: [pod-envfrom.yaml](pod-envfrom.yaml)

```bash
kubectl apply -f kubernetes/week-3/ConfigMap/pod-envfrom.yaml
kubectl wait --for=condition=Ready pod/cm-envfrom -n ecommerce-dev --timeout=60s
```

Ortam değişkenlerini kontrol et. `demo` ve `Information` yazmalı:

```bash
kubectl exec cm-envfrom -n ecommerce-dev -- printenv APP_MODE LOG_LEVEL
```

> `envFrom` ile anahtar adları geçerli bir ortam değişkeni adı olmalıdır. `appsettings.Production.json` gibi `.` içeren anahtarlar ortam değişkeni olamaz, Kubernetes bunları atlar. Dosya gibi içerikler için Yol 3 kullanılır.

### Yol 3: Dosya olarak bağla (`volume` + `volumeMounts`)

Üç bağlama gösterilir. Dosya: [pod-volume.yaml](pod-volume.yaml)

- `app-file` ConfigMap'indeki `appsettings.Production.json` anahtarı, `subPath` ile container içinde tek bir dosya olarak (`/app/appsettings.Production.json`) bağlanır.
- `app-yaml` ConfigMap'i `/etc/app-config` klasörü olarak bağlanır. Her anahtar bir dosya olur.
- `app-json` ConfigMap'i `/etc/app-json` klasörü olarak bağlanır. YAML içine yazdığımız JSON, `appsettings.Development.json` dosyası olur.

```bash
kubectl apply -f kubernetes/week-3/ConfigMap/pod-volume.yaml
kubectl wait --for=condition=Ready pod/cm-volume -n ecommerce-dev --timeout=60s
```

Dosyaları container içinde göster:

```bash
kubectl exec cm-volume -n ecommerce-dev -- cat /app/appsettings.Production.json
kubectl exec cm-volume -n ecommerce-dev -- ls /etc/app-config
kubectl exec cm-volume -n ecommerce-dev -- cat /etc/app-config/APP_MODE
kubectl exec cm-volume -n ecommerce-dev -- cat /etc/app-json/appsettings.Development.json
```

## ConfigMap değişince ne olur

ConfigMap'in değerini değiştir:

```bash
kubectl patch configmap app-yaml -n ecommerce-dev --type merge -p '{"data":{"APP_MODE":"production"}}'
```

Kullanım yoluna göre sonuç farklıdır:

| Kullanım | ConfigMap değişince |
|---|---|
| Ortam değişkeni (`env`, `envFrom`) | Güncellenmez. Pod yeniden oluşturulmalıdır |
| Volume, klasör olarak bağlı | Kubelet bir süre sonra (genelde 1 dakika içinde) dosyayı günceller |
| Volume, `subPath` ile bağlı | Güncellenmez. Pod yeniden oluşturulmalıdır |

Aşağıdaki komutları 1-2 dakika sonra çalıştır. Ortam değişkeni eski değerde kalır, klasör olarak bağlı dosya yeni değeri gösterir:

```bash
kubectl exec cm-envfrom -n ecommerce-dev -- printenv APP_MODE
kubectl exec cm-volume -n ecommerce-dev -- cat /etc/app-config/APP_MODE
```

Uygulama dosyadaki değişikliği kendisi izlemiyorsa yeni değer için Pod'un yeniden başlatılması gerekir.

## Notlar

- Bir ConfigMap'in toplam boyutu 1 MiB ile sınırlıdır.
- Pod'un kullandığı ConfigMap Pod'dan önce oluşturulmalıdır. Yoksa Pod `CreateContainerConfigError` (env) ya da `ContainerCreating` (volume) durumunda bekler.
- `ecommerce-dev` namespace'inde ResourceQuota ile ConfigMap sayısı sınırlıdır (bkz. [namespace.md](../namespace/namespace.md)).
- Bir ConfigMap `immutable: true` ile değiştirilemez hâle getirilebilir. Bu, yanlışlıkla değişikliği önler ve kubelet'in izleme yükünü azaltır.

## Temizle

```bash
kubectl delete pod cm-env cm-envfrom cm-volume -n ecommerce-dev
kubectl delete configmap app-literal app-file app-envfile app-yaml app-json -n ecommerce-dev
```
