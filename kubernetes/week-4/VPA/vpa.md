# VerticalPodAutoscaler (VPA)

VPA, Pod'ların **CPU ve memory request** değerlerini gerçek kullanıma bakarak önerir veya otomatik ayarlar. HPA Pod sayısını değiştirir, VPA tek Pod'un boyutunu değiştirir.

Üç bileşeni vardır:

| Bileşen | Görevi |
|---|---|
| Recommender | Geçmiş kullanımdan öneri üretir |
| Updater | Önerisi uymayan Pod'u evict eder |
| Admission Controller | Yeni Pod açılırken request değerlerini önerilene çevirir |

> VPA ve HPA aynı metrik (CPU) üzerinde birlikte kullanılmaz, birbirlerini bozarlar.

Komutlar `kubernetes/week-6` klasöründen çalıştırılır.

| Dosya | Görevi |
|---|---|
| [cpu-hog-deployment.yaml](cpu-hog-deployment.yaml) | Sürekli CPU tüketen, request'i bilerek çok düşük Deployment |
| [vpa-off.yaml](vpa-off.yaml) | Sadece öneri üretir (`updateMode: "Off"`) |
| [vpa-auto.yaml](vpa-auto.yaml) | Pod'u yeniden oluşturarak uygular (`updateMode: "Recreate"`) |

## Adım 1: metrics-server ve VPA'yı kur

metrics-server, [HPA dokümanındaki](../../week-4/hpa/hpa.md) adımlarla kurulmuş olmalıdır:

```bash
kubectl top nodes
```

VPA için resmi helm chart'ı kullan (sürümü sabitle):

```bash
helm repo add autoscaler https://kubernetes.github.io/autoscaler
helm repo update
helm install vpa autoscaler/vertical-pod-autoscaler --version 0.15.0 -n kube-system
kubectl get pods -n kube-system | grep vpa
kubectl get crd | grep verticalpodautoscalers
```

Recommender, updater ve admission-controller Pod'ları `Running` olmalıdır.

## Adım 2: Öneri modu (Off)

```bash
kubectl apply -f Namespace/namespace.yaml
kubectl apply -f VPA/cpu-hog-deployment.yaml -f VPA/vpa-off.yaml
kubectl rollout status deploy/cpu-hog -n scheduling-lab
```

Yaklaşık 1-2 dakika sonra öneri oluşur:

```bash
kubectl get vpa -n scheduling-lab
kubectl describe vpa cpu-hog-vpa -n scheduling-lab
```

Örnek çıktı:

```
NAME          MODE   CPU    MEM     PROVIDED   AGE
cpu-hog-vpa   Off    587m   250Mi   True       90s
```

`describe` içindeki alanlar:

- `Lower Bound`: bunun altına inme
- `Target`: önerilen request
- `Upper Bound`: bunun üstü gereksiz

Pod'ların request'i `10m` kalır. `Off` modu Pod'lara dokunmaz. Production'a geçerken güvenli başlangıçtır.

## Adım 3: Otomatik uygulama (Recreate)

`vpa-auto.yaml` önerinin üst sınırını `maxAllowed` ile 500m / 64Mi'ye kısıtlar ve `controlledValues: RequestsOnly` kullanır:

```bash
kubectl apply -f VPA/vpa-auto.yaml
```

1-3 dakika içinde updater bir Pod'u evict eder, yeni Pod önerilen request ile açılır:

```bash
kubectl get events -n scheduling-lab | grep -i vpa
kubectl get pods -n scheduling-lab -l app=cpu-hog \
  -o 'custom-columns=NAME:.metadata.name,CPU:.spec.containers[0].resources.requests.cpu,MEM:.spec.containers[0].resources.requests.memory,CPULIM:.spec.containers[0].resources.limits.cpu'
```

```
EvictedByVPA  Pod was evicted by VPA Updater to apply resource recommendation.

NAME                       CPU    MEM    CPULIM
cpu-hog-5c64bf8594-56xch   10m    16Mi   500m     <- henüz eski
cpu-hog-5c64bf8594-qvbpf   500m   64Mi   500m     <- VPA ayarladı
```

Updater Pod'ları tek tek yeniler, bu yüzden hepsinin güncellenmesi birkaç dakika sürer. Eski Pod, `busybox` döngüsü SIGTERM'i yakalamadığı için `Terminating`'de 30 sn kalabilir.

> **`controlledValues: RequestsOnly` neden önemli?** Varsayılan `RequestsAndLimits` modunda VPA, request:limit oranını korur. Burada request 10m → 500m (50 kat) olduğu için limit de 500m → **25 çekirdek**, memory limit 64Mi → 256Mi'ye çıktı. Limit'i kendin yönetmek istiyorsan `RequestsOnly` kullan.

## updateMode değerleri

| Değer | Davranış |
|---|---|
| `Off` | Sadece öneri üretir |
| `Initial` | Sadece yeni açılan Pod'lara uygular, çalışanlara dokunmaz |
| `Recreate` | Pod'u evict edip yeniden oluşturur |
| `Auto` | Mevcut sürümde `Recreate` ile aynı davranır, ileride in-place resize kullanabilir |

## Temizlik

```bash
kubectl delete -f VPA/vpa-auto.yaml -f VPA/cpu-hog-deployment.yaml
# VPA'yı da kaldırmak istersen:
helm uninstall vpa -n kube-system
```
