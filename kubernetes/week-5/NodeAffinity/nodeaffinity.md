# Node Affinity

Node Affinity, `nodeSelector`'ın gelişmiş halidir. Pod'un hangi node'lara yerleşebileceğini **node etiketleriyle** belirler, ama `In`, `NotIn`, `Exists`, `DoesNotExist`, `Gt`, `Lt` operatörlerini ve "tercih edilir ama zorunlu değil" kuralını destekler.

İki tür kural vardır:

| Kural | Anlamı |
|---|---|
| `requiredDuringSchedulingIgnoredDuringExecution` | **Zorunlu.** Uyan node yoksa Pod `Pending` kalır (nodeSelector gibi) |
| `preferredDuringSchedulingIgnoredDuringExecution` | **Tercih.** Uyan node varsa orası seçilir, yoksa Pod başka node'a yerleşir. `weight` (1-100) ile puanlanır |

`IgnoredDuringExecution`: Pod çalışmaya başladıktan sonra node etiketi değişirse Pod silinmez, kural yalnızca yeni yerleştirmelerde geçerlidir.

Komutlar `kubernetes/week-5` klasöründen çalıştırılır.

| Dosya | Görevi |
|---|---|
| [1-pod-required.yaml](1-pod-required.yaml) | `disktype` değeri `ssd` veya `nvme` olan node'a giden Pod (zorunlu) |
| [2-pod-required-pending.yaml](2-pod-required-pending.yaml) | Hiçbir node'da olmayan `accelerator=gpu` isteyen Pod (Pending kalır) |
| [3-pod-preferred.yaml](3-pod-preferred.yaml) | SSD'li node'u tercih eden Pod, yoksa herhangi bir node |
| [4-deployment-affinity.yaml](4-deployment-affinity.yaml) | Zorunlu + tercih birlikte: control-plane'e gitme, SSD'li node'a git |

## Adım 1: Hazırlık

```bash
kubectl apply -f Namespace/namespace.yaml
kubectl label node desktop-worker disktype=ssd
kubectl get nodes -L disktype
```

## Adım 2: Zorunlu kural (`required`)

```bash
kubectl apply -f NodeAffinity/1-pod-required.yaml
kubectl get pod web-required -n scheduling-lab -o wide
```

Pod `desktop-worker` node'una düşer. `operator: In` ile birden fazla değer verilebilir. nodeSelector bunu yapamaz.

## Adım 3: Uyan node yok

```bash
kubectl apply -f NodeAffinity/2-pod-required-pending.yaml
kubectl get pod web-required-gpu -n scheduling-lab
kubectl describe pod web-required-gpu -n scheduling-lab
```

Pod `Pending` kalır. `Events` bölümü:

```
Warning  FailedScheduling  default-scheduler  0/3 nodes are available: 1 node(s) had untolerated taint(s), 2 node(s) didn't match Pod's node affinity/selector.
```

## Adım 4: Tercih kuralı (`preferred`)

```bash
kubectl apply -f NodeAffinity/3-pod-preferred.yaml
kubectl get pod web-preferred -n scheduling-lab -o wide
```

Pod `desktop-worker`'a gider (`disktype=ssd`, weight 80, `desktop-worker2` hostname kuralının weight 20'sinden ağır basar).

Şimdi etiketi kaldırıp Pod'u yeniden oluştur:

```bash
kubectl label node desktop-worker disktype-
kubectl delete pod web-preferred -n scheduling-lab
kubectl apply -f NodeAffinity/3-pod-preferred.yaml
kubectl get pod web-preferred -n scheduling-lab -o wide
```

SSD'li node kalmadı, `desktop-worker2` hostname kuralı (weight 20) puan kazandırır ve Pod oraya yerleşir. Pod `Pending` kalmaz. `required` ile arasındaki fark budur.

## Adım 5: Deployment ile zorunlu + tercih

```bash
kubectl label node desktop-worker disktype=ssd
kubectl apply -f NodeAffinity/4-deployment-affinity.yaml
kubectl get pods -n scheduling-lab -l app=web-affinity -o wide
```

- `required` + `DoesNotExist`: control-plane etiketi olan node elenir.
- `preferred` + `Exists`: `disktype` etiketi olan node tercih edilir.

4 replikanın hepsi `desktop-worker`'a yerleşir.

## nodeSelector ile karşılaştırma

| | nodeSelector | Node Affinity |
|---|---|---|
| Eşitlik (`key=value`) | Evet | Evet (`In`) |
| `NotIn`, `Exists`, `DoesNotExist`, `Gt`, `Lt` | Hayır | Evet |
| Tercih (zorunlu değil) | Hayır | Evet (`preferred`) |
| Birden fazla alternatif (OR) | Hayır | Evet (birden fazla `nodeSelectorTerms`) |

- `nodeSelectorTerms` listesindeki elemanlar **OR**, bir `matchExpressions` içindekiler **AND** ile birleşir.
- Bir node'u Pod'lara **kapatmak** için Node Affinity değil Taint/Toleration kullanılır.

## Temizlik

```bash
kubectl delete -f NodeAffinity/
kubectl label node desktop-worker disktype-
```
