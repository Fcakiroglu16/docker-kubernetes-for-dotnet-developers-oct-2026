# Label

Label, Kubernetes nesnelerine eklenen `anahtar=değer` çiftleridir. Nesneleri gruplamak ve filtrelemek için kullanılır. Bu dokümanda Pod ve Namespace üzerinde `kubectl` ile label'ların nasıl oluşturulup okunduğu, güncellendiği ve silindiği anlatılır.

## Hazırlık: label'lı Pod'lar oluştur

Önce `Microservice1.API` image'ının build edilmiş olması gerekir (bkz. [pod.md](../pod/pod.md)).

### YAML ile label'lı Pod

Label'lar `metadata.labels` altında tanımlanır. Dosya: [microservice1-labeled-pod.yaml](microservice1-labeled-pod.yaml)

```bash
kubectl apply -f kubernetes/week-3/Label/microservice1-labeled-pod.yaml
```

### Komutla label'lı Pod

`--labels` parametresiyle Pod oluşturulurken label verilebilir. Filtreleme örnekleri için farklı label'lara sahip ikinci bir Pod oluşturuyoruz:

```bash
kubectl run microservice1-labeled-2 -n ecommerce-dev \
  --image=microservice1-api:1.6 --image-pull-policy=IfNotPresent \
  --labels="app=microservice1-api,tier=backend,environment=staging,version=v2" \
  --overrides='{"spec":{"containers":[{"name":"microservice1-labeled-2","image":"microservice1-api:1.6","imagePullPolicy":"IfNotPresent","resources":{"requests":{"cpu":"100m","memory":"128Mi"},"limits":{"cpu":"500m","memory":"256Mi"}}}]}}'
```

> `--overrides` içindeki `resources`, `ecommerce-dev` namespace'inde ResourceQuota olduğu için zorunludur (bkz. [namespace.md](../namespace/namespace.md)).

Pod'ların hazır olduğunu kontrol et:

```bash
kubectl get pods -n ecommerce-dev --show-labels
```

## Read: Label'ları göster

Tüm label'ları göster:

```bash
kubectl get pods -n ecommerce-dev --show-labels
```

Seçili label'ları ayrı sütun olarak göster (`-L`). Label'ı olmayan Pod'un sütunu boş kalır:

```bash
kubectl get pods -n ecommerce-dev -L app,version,environment
```

Tek bir Pod'un label'larını JSON olarak al:

```bash
kubectl get pod microservice1-labeled -n ecommerce-dev -o jsonpath='{.metadata.labels}'
```

### Label selector ile filtrele (`-l`)

Eşitlik tabanlı selector:

```bash
kubectl get pods -n ecommerce-dev -l version=v1
kubectl get pods -n ecommerce-dev -l app=microservice1-api,tier=backend   # virgül = AND
kubectl get pods -n ecommerce-dev -l version!=v2
```

Küme tabanlı selector:

```bash
kubectl get pods -n ecommerce-dev -l 'environment in (development,staging)'
kubectl get pods -n ecommerce-dev -l 'version notin (v1)'
kubectl get pods -n ecommerce-dev -l version      # version label'ı olanlar
kubectl get pods -n ecommerce-dev -l '!version'   # version label'ı olmayanlar
```

> `!=` ve `notin`, ilgili label'ı hiç taşımayan nesneleri de getirir. Örneğin `version notin (v1)` sonucunda `version` label'ı olmayan Pod'lar da listelenir.

> Selector ifadelerinde `!`, parantez ve boşluk geçiyorsa tek tırnak kullan. Yoksa shell bunları kendi yorumlar.

Namespace'leri de label'a göre listeleyebilirsin:

```bash
kubectl get namespaces -l team=ecommerce
```

## Create: Label ekle

Bir Pod'a bir veya birden fazla label ekle:

```bash
kubectl label pod microservice1-labeled -n ecommerce-dev team=ecommerce owner=fatih
```

Selector'a uyan tüm Pod'lara label ekle:

```bash
kubectl label pods -n ecommerce-dev -l tier=backend release=2026-10
```

Namespace'teki tüm Pod'lara label ekle:

```bash
kubectl label pods -n ecommerce-dev --all managed-by=kubectl
```

Namespace'e label ekle:

```bash
kubectl label namespace ecommerce-dev owner=platform
```

Değişikliği uygulamadan önce ne olacağını gör (`--dry-run`). Hiçbir şey değişmez. Çıktı tüm Pod YAML'ıdır, bu yüzden sadece label kısmını göstermek için `sed` kullanıyoruz:

```bash
kubectl label pod microservice1-labeled -n ecommerce-dev stage=test --dry-run=client -o yaml | sed -n '/^  labels:/,/^  name:/p'
```

## Update: Label değerini değiştir

Var olan bir label'ın değerini değiştirmek için `--overwrite` gerekir. Olmazsa hata verir:

```bash
kubectl label pod microservice1-labeled -n ecommerce-dev environment=staging
# error: 'environment' already has a value (development), and --overwrite is false
```

```bash
kubectl label pod microservice1-labeled -n ecommerce-dev environment=staging --overwrite
```

Toplu güncelleme için de `--overwrite` kullanılır:

```bash
kubectl label pods -n ecommerce-dev -l tier=backend release=2026-11 --overwrite
```

Sonucu kontrol et:

```bash
kubectl get pods -n ecommerce-dev -L environment,release
```

## Delete: Label sil

Label adının sonuna `-` koyulur:

```bash
kubectl label pod microservice1-labeled -n ecommerce-dev owner-
```

Birden fazla label'ı birlikte, tüm Pod'lardan sil. Bir Pod'da o label yoksa `label "release" not found.` uyarısı verir, diğer Pod'larda silme devam eder:

```bash
kubectl label pods -n ecommerce-dev --all managed-by- release-
```

Namespace'ten label sil:

```bash
kubectl label namespace ecommerce-dev owner-
```

## Notlar

- Label anahtarı ve değeri en fazla 63 karakter olabilir. Harf, rakam, `-`, `_` ve `.` içerebilir ve harf ya da rakamla başlayıp bitmelidir.
- `kubectl label` ile eklenen label'lar yalnızca o nesnede kalır. Pod silinip YAML'dan yeniden oluşturulursa YAML'daki label'lara döner.
- `kubectl apply` ile yeniden uygulanan YAML, `kubectl label` ile elle eklenmiş fazladan label'ları silmez.

## Temizle

```bash
kubectl delete pod microservice1-labeled microservice1-labeled-2 -n ecommerce-dev
```
