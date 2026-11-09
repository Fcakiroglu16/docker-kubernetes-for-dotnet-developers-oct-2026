# Pod Affinity ve Anti-Affinity

Node Affinity node **etiketlerine** bakar. Pod Affinity ise node'daki **diğer Pod'ların etiketlerine** bakar:

| Kural | Anlamı | Örnek |
|---|---|---|
| `podAffinity` | Şu Pod'ların **yanına** yerleş | Uygulama, cache ile aynı node'da çalışsın (düşük gecikme) |
| `podAntiAffinity` | Şu Pod'lardan **uzak dur** | Replikalar farklı node'lara dağılsın (yüksek erişilebilirlik) |

`required...` (zorunlu) ve `preferred...` (tercih, `weight` ile) türleri Node Affinity ile aynıdır.

## topologyKey

"Yanına" ne demek? Bunu `topologyKey` belirler. Node etiketi olan bir anahtardır, aynı değere sahip node'lar aynı "alan" sayılır:

| topologyKey | "Aynı yer" |
|---|---|
| `kubernetes.io/hostname` | Aynı node |
| `topology.kubernetes.io/zone` | Aynı zone (bulut ortamlarında) |

Bu cluster'da zone etiketi olmadığı için örneklerde `kubernetes.io/hostname` kullanıldı.

Komutlar `kubernetes/week-5` klasöründen çalıştırılır.

| Dosya | Görevi |
|---|---|
| [1-deployment-cache.yaml](1-deployment-cache.yaml) | Referans Pod: `app=cache` etiketli 1 replika |
| [2-deployment-web-affinity.yaml](2-deployment-web-affinity.yaml) | `app=cache` ile aynı node'a yerleşen 3 replika (Pod Affinity) |
| [3-pod-affinity-pending.yaml](3-pod-affinity-pending.yaml) | Hiçbir yerde olmayan `app=db` Pod'unu arar (Pending kalır) |
| [4-deployment-anti-affinity.yaml](4-deployment-anti-affinity.yaml) | Her node'da en fazla 1 replika (zorunlu Anti-Affinity) |
| [5-deployment-preferred-anti-affinity.yaml](5-deployment-preferred-anti-affinity.yaml) | Farklı node'lara dağılmayı tercih eder (esnek Anti-Affinity) |

## Adım 1: Referans Pod

```bash
kubectl apply -f Namespace/namespace.yaml
kubectl apply -f PodAffinity/1-deployment-cache.yaml
kubectl get pods -n scheduling-lab -l app=cache -o wide
```

`cache` Pod'u hangi node'a düştüyse not et (testte `desktop-worker`).

## Adım 2: Pod Affinity ile yanına yerleş

```bash
kubectl apply -f PodAffinity/2-deployment-web-affinity.yaml
kubectl get pods -n scheduling-lab -l app=web-near-cache -o wide
```

3 replikanın hepsi `cache` ile aynı node'a yerleşir. Node'un kendi etiketine değil, orada `app=cache` Pod'unun olmasına bakılır.

## Adım 3: Aranan Pod yok

```bash
kubectl apply -f PodAffinity/3-pod-affinity-pending.yaml
kubectl describe pod web-near-db -n scheduling-lab
```

Pod `Pending` kalır:

```
Warning  FailedScheduling  default-scheduler  0/3 nodes are available: 1 node(s) had untolerated taint(s), 2 node(s) didn't match pod affinity rules.
```

`app=db` etiketli bir Pod çalışmaya başlarsa Pod otomatik yerleşir.

## Adım 4: Zorunlu Anti-Affinity

```bash
kubectl apply -f PodAffinity/4-deployment-anti-affinity.yaml
kubectl get pods -n scheduling-lab -l app=web-spread -o wide
```

Her node'a en fazla 1 replika düşer. Yerleşebilir 2 worker olduğu için 3 replikadan **biri `Pending` kalır**. Replika sayısı node sayısını aşarsa zorunlu Anti-Affinity bu sorunu çıkarır.

## Adım 5: Esnek Anti-Affinity

```bash
kubectl apply -f PodAffinity/5-deployment-preferred-anti-affinity.yaml
kubectl get pods -n scheduling-lab -l app=web-spread-soft -o wide
```

4 replika iki node'a 2+2 dağılır. Hiçbiri `Pending` kalmaz, node sayısı yetmese de aynı node'a yerleşebilirler. Yüksek erişilebilirlik isteyen ama kapasiteyi riske atmak istemeyen uygulamalar için daha güvenli seçimdir.

## Dikkat edilecekler

- `labelSelector` Pod'un **kendi** etiketini de eşleştirebilir. Anti-Affinity örneğinde (`app: web-spread`) bu kasıtlıdır: "kendi kopyalarımdan uzak dur".
- Pod Affinity, scheduler için pahalıdır (tüm Pod'ları tarar). Çok büyük cluster'larda (yüzlerce node) yavaşlatabilir.
- Replikaları node/zone'lara **eşit** dağıtmak için bunun yerine Topology Spread Constraints daha uygundur. Anti-Affinity "en fazla 1" gibi sert sınırlar koyar.
- Aynı namespace'teki Pod'lara bakar. Başka namespace için `namespaces` veya `namespaceSelector` alanı gerekir.

## Temizlik

```bash
kubectl delete -f PodAffinity/ --ignore-not-found
```
