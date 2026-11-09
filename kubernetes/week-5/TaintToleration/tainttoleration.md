# Taint ve Toleration

`nodeSelector` ve Node Affinity Pod'un **node'u seçmesini** sağlar. Taint ise tersine çalışır: **node, Pod'ları reddeder**. Node'a taint konur, yalnızca o taint'e uyan `toleration`'ı olan Pod'lar o node'a girebilir.

Taint formatı: `key=value:effect`

| Effect | Anlamı |
|---|---|
| `NoSchedule` | Toleration'ı olmayan yeni Pod'lar yerleşmez. Çalışan Pod'lara dokunulmaz |
| `PreferNoSchedule` | Scheduler kaçınır ama zorunlu değil (yumuşak `NoSchedule`) |
| `NoExecute` | Yeni Pod'lar yerleşmez **ve** çalışan, toleration'ı olmayan Pod'lar node'dan çıkarılır |

Toleration operatörleri: `Equal` (key ve value eşit) ve `Exists` (sadece key var olsun, value verilmez).

Komutlar `kubernetes/week-5` klasöründen çalıştırılır.

| Dosya | Görevi |
|---|---|
| [1-deployment-no-toleration.yaml](1-deployment-no-toleration.yaml) | Toleration'sız 4 replika, taint'li node'u atlar |
| [2-pod-no-toleration-pending.yaml](2-pod-no-toleration-pending.yaml) | Taint'li node'a gitmek ister ama toleration'ı yok (Pending kalır) |
| [3-pod-toleration.yaml](3-pod-toleration.yaml) | `dedicated=gpu` toleration'ı olan Pod, taint'li node'a girer |
| [4-pod-noexecute-toleration.yaml](4-pod-noexecute-toleration.yaml) | `NoExecute` taint'inde `tolerationSeconds: 20` kadar kalır |

## Adım 1: Taint ekle

```bash
kubectl apply -f Namespace/namespace.yaml
kubectl taint node desktop-worker dedicated=gpu:NoSchedule
kubectl describe node desktop-worker | grep Taints
```

Control-plane'de zaten `node-role.kubernetes.io/control-plane:NoSchedule` taint'i vardır. Pod'ların oraya gitmemesinin sebebi budur.

## Adım 2: Toleration'sız Pod'lar

```bash
kubectl apply -f TaintToleration/1-deployment-no-toleration.yaml
kubectl get pods -n scheduling-lab -l app=web-plain -o wide
```

4 replikanın hepsi `desktop-worker2`'ye gider. `desktop-worker` taint'li, control-plane de taint'li olduğu için yalnızca bir node kalır.

## Adım 3: Taint'li node'u isteyen ama toleration'ı olmayan Pod

```bash
kubectl apply -f TaintToleration/2-pod-no-toleration-pending.yaml
kubectl get pod web-no-toleration -n scheduling-lab
kubectl describe pod web-no-toleration -n scheduling-lab
```

Pod `Pending` kalır. `Events` bölümü:

```
Warning  FailedScheduling  default-scheduler  0/3 nodes are available: 1 node(s) didn't match Pod's node affinity/selector, 2 node(s) had untolerated taint(s).
```

## Adım 4: Toleration ile giriş

```bash
kubectl apply -f TaintToleration/3-pod-toleration.yaml
kubectl get pod web-toleration -n scheduling-lab -o wide
```

Pod `desktop-worker`'a yerleşir.

**Önemli:** Toleration sadece **izin verir**, Pod'u o node'a **çekmez**. Pod'un `desktop-worker2`'ye gitmesi de mümkündür. Bu yüzden YAML'da `nodeSelector` ile node'u ayrıca seçtik. Gerçek kullanımda "bu node sadece GPU işleri için" kuralı için taint + toleration + nodeSelector/Node Affinity birlikte kullanılır.

## Adım 5: NoExecute ve tolerationSeconds

```bash
kubectl apply -f TaintToleration/4-pod-noexecute-toleration.yaml
kubectl taint node desktop-worker2 maintenance=true:NoExecute
kubectl get pods -n scheduling-lab -o wide
```

Hemen sonra:
- `web-plain-*` Pod'ları toleration'sız olduğu için `desktop-worker2`'den anında çıkarılır. Deployment yeni Pod açar ama yerleşecek node kalmadığı için `Pending` olurlar.
- `web-noexecute` hâlâ `Running`. Toleration'ı var.

20 saniye sonra:

```bash
kubectl get pods -n scheduling-lab
```

`web-noexecute` de çıkarılıp silinir. `tolerationSeconds` toleration'ın süresini belirler. `tolerationSeconds` yoksa Pod sonsuza kadar kalır. Node'un `NotReady` olması gibi durumlarda Kubernetes bu mekanizmayı otomatik kullanır (varsayılan 300 sn).

## Taint'i kaldırma

Taint'in sonuna `-` eklenir:

```bash
kubectl taint node desktop-worker2 maintenance=true:NoExecute-
kubectl taint node desktop-worker dedicated=gpu:NoSchedule-
```

`NoSchedule` taint'ini kaldırınca `Pending` Pod'lar otomatik yerleşir.

## nodeSelector / Affinity ile fark

| | nodeSelector / Node Affinity | Taint / Toleration |
|---|---|---|
| Karar kimde | Pod "şu node'a git" der | Node "şu Pod'lar gelsin" der |
| Varsayılan | Etiketsiz Pod her yere gidebilir | Toleration'sız Pod taint'li node'a giremez |
| Çalışan Pod'u çıkarır mı | Hayır | Evet (`NoExecute`) |

Sık kullanım: özel donanımlı (GPU) node'ları ayırmak, bakıma alınacak node'u boşaltmak (`kubectl drain` buna benzer çalışır), control-plane'i korumak.

## Temizlik

```bash
kubectl delete -f TaintToleration/ --ignore-not-found
kubectl taint node desktop-worker dedicated=gpu:NoSchedule-
kubectl taint node desktop-worker2 maintenance=true:NoExecute-
```
