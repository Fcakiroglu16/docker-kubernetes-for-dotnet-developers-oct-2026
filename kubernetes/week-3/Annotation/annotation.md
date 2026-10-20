# Annotation

Annotation, Label gibi Kubernetes nesnelerine eklenen `anahtar=değer` çiftleridir. Fark şudur: Label nesneleri **seçmek ve gruplamak** için, Annotation ise nesneye **ek bilgi iliştirmek** için kullanılır. Bu dokümanda Pod ve Namespace üzerinde `kubectl` ile annotation'ların nasıl oluşturulup okunduğu, güncellendiği ve silindiği anlatılır.

| | Label | Annotation |
|---|---|---|
| Amaç | Nesneleri seçmek, gruplamak | Nesneye bilgi eklemek (sahip, açıklama, commit, URL, araç ayarları) |
| Selector ile filtrelenir mi | Evet (`-l`) | Hayır |
| Değer sınırı | Kısa, 63 karakter, sınırlı karakter | Uzun olabilir, her karakter kullanılabilir (JSON bile) |
| Örnek | `app=microservice1-api` | `ecommerce.io/git-commit=a1b2c3d` |

## Hazırlık: annotation'lı Pod'lar oluştur

Önce `Microservice1.API` image'ının build edilmiş olması gerekir (bkz. [pod.md](../pod/pod.md)).

### YAML ile annotation'lı Pod

Annotation'lar `metadata.annotations` altında tanımlanır. Değerler her zaman string'dir. Dosya: [microservice1-annotated-pod.yaml](microservice1-annotated-pod.yaml)

```bash
kubectl apply -f kubernetes/week-3/Annotation/microservice1-annotated-pod.yaml
```

### Annotation'sız ikinci Pod

Toplu işlemleri denemek için annotation'ı olmayan ikinci bir Pod oluşturuyoruz:

```bash
kubectl run microservice1-plain -n ecommerce-dev \
  --image=microservice1-api:1.6 --image-pull-policy=IfNotPresent \
  --labels="app=microservice1-api,tier=backend" \
  --overrides='{"spec":{"containers":[{"name":"microservice1-plain","image":"microservice1-api:1.6","imagePullPolicy":"IfNotPresent","resources":{"requests":{"cpu":"100m","memory":"128Mi"},"limits":{"cpu":"500m","memory":"256Mi"}}}]}}'
```

> `--overrides` içindeki `resources`, `ecommerce-dev` namespace'inde ResourceQuota olduğu için zorunludur (bkz. [namespace.md](../namespace/namespace.md)).

## Read: Annotation'ları göster

`describe` çıktısında `Annotations:` bölümü görünür:

```bash
kubectl describe pod microservice1-annotated -n ecommerce-dev | grep -A8 '^Annotations:'
```

Tüm annotation'ları JSON olarak al:

```bash
kubectl get pod microservice1-annotated -n ecommerce-dev -o jsonpath='{.metadata.annotations}'
```

Tek bir annotation'ın değerini al. Anahtardaki `.` karakteri `\.` ile kaçırılmalıdır:

```bash
kubectl get pod microservice1-annotated -n ecommerce-dev -o jsonpath='{.metadata.annotations.ecommerce\.io/git-commit}'
```

Annotation'ları YAML çıktısında sadece ilgili bölümüyle göster:

```bash
kubectl get pod microservice1-annotated -n ecommerce-dev -o yaml | sed -n '/^  annotations:/,/^  creationTimestamp:/p'
```

Annotation'ı tablo sütunu olarak göster (`custom-columns`). Annotation'ı olmayan Pod'un sütunu `<none>` olur:

```bash
kubectl get pods -n ecommerce-dev -o custom-columns='NAME:.metadata.name,OWNER:.metadata.annotations.ecommerce\.io/owner,COMMIT:.metadata.annotations.ecommerce\.io/git-commit'
```

### Annotation'a göre filtrele

Annotation'lar için `-l` selector'ı çalışmaz. Filtrelemek için `jsonpath` filtresi kullanılır:

```bash
kubectl get pods -n ecommerce-dev -o jsonpath="{range .items[?(@.metadata.annotations['ecommerce\.io/git-commit']=='a1b2c3d')]}{.metadata.name}{'\n'}{end}"
```

## Create: Annotation ekle

Bir Pod'a bir veya birden fazla annotation ekle. Değerinde boşluk varsa tırnak içine al:

```bash
kubectl annotate pod microservice1-plain -n ecommerce-dev ecommerce.io/owner=ecommerce-team@example.com ecommerce.io/note="ilk deneme"
```

Selector'a uyan tüm Pod'lara annotation ekle:

```bash
kubectl annotate pods -n ecommerce-dev -l tier=backend ecommerce.io/release=2026-10
```

Namespace'teki tüm Pod'lara annotation ekle:

```bash
kubectl annotate pods -n ecommerce-dev --all ecommerce.io/managed-by=kubectl
```

Namespace'e annotation ekle:

```bash
kubectl annotate namespace ecommerce-dev ecommerce.io/cost-center=cc-1234
```

Değişikliği uygulamadan önce ne olacağını gör (`--dry-run`). Hiçbir şey değişmez:

```bash
kubectl annotate pod microservice1-plain -n ecommerce-dev ecommerce.io/stage=test --dry-run=client -o yaml | sed -n '/^  annotations:/,/^  creationTimestamp:/p'
```

Sonucu kontrol et:

```bash
kubectl describe pod microservice1-plain -n ecommerce-dev | grep -A8 '^Annotations:'
```

## Update: Annotation değerini değiştir

Var olan bir annotation'ın değerini değiştirmek için `--overwrite` gerekir. Olmazsa hata verir:

```bash
kubectl annotate pod microservice1-plain -n ecommerce-dev ecommerce.io/note="ikinci deneme"
# error: --overwrite is false but found the following declared annotation(s): 'ecommerce.io/note' already has a value (ilk deneme)
```

```bash
kubectl annotate pod microservice1-plain -n ecommerce-dev ecommerce.io/note="ikinci deneme" --overwrite
```

Toplu güncelleme için de `--overwrite` kullanılır:

```bash
kubectl annotate pods -n ecommerce-dev -l tier=backend ecommerce.io/release=2026-11 --overwrite
```

Sonucu kontrol et:

```bash
kubectl get pods -n ecommerce-dev -o custom-columns='NAME:.metadata.name,NOTE:.metadata.annotations.ecommerce\.io/note,RELEASE:.metadata.annotations.ecommerce\.io/release'
```

## Delete: Annotation sil

Annotation adının sonuna `-` koyulur:

```bash
kubectl annotate pod microservice1-plain -n ecommerce-dev ecommerce.io/note-
```

Birden fazla annotation'ı birlikte, tüm Pod'lardan sil. Bir Pod'da o annotation yoksa hata vermez, sessizce geçer (`kubectl label` ise `label "..." not found.` uyarısı verir):

```bash
kubectl annotate pods -n ecommerce-dev --all ecommerce.io/managed-by- ecommerce.io/release-
```

Namespace'ten annotation sil:

```bash
kubectl annotate namespace ecommerce-dev ecommerce.io/cost-center-
```

## Notlar

- `kubectl apply` ile oluşturulan nesnelerde Kubernetes kendiliğinden `kubectl.kubernetes.io/last-applied-configuration` annotation'ını ekler. LimitRange'in eklediği `kubernetes.io/limit-ranger` gibi sistem annotation'ları da `describe` çıktısında görünür. Bunları silme.
- Annotation anahtarı `alan-adı/anahtar` biçiminde olabilir. Alan adı kısmı sana ait bir domain olmalı (`ecommerce.io/...`). `kubernetes.io/` ve `k8s.io/` önekleri Kubernetes'e ayrılmıştır.
- Annotation'lar araçlar tarafından da okunur. Örneğin Prometheus (`prometheus.io/scrape`) ve Ingress denetleyicileri ayarlarını annotation'dan alır. Bu yüzden annotation değiştirmek bazen davranışı değiştirir.
- Bir nesnenin tüm annotation'larının toplam boyutu 256 KB ile sınırlıdır.

## Temizle

```bash
kubectl delete pod microservice1-annotated microservice1-plain -n ecommerce-dev
```
