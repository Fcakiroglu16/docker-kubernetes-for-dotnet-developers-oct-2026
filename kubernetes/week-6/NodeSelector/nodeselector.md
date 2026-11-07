# nodeSelector

`nodeSelector`, Pod'un hangi node'larda çalışabileceğini **node etiketleriyle** belirler. Pod'un `spec.nodeSelector` alanındaki tüm etiketler node'da bulunmalıdır, yoksa scheduler Pod'u o node'a yerleştirmez. Bulunamazsa Pod `Pending` kalır.

Komutlar `kubernetes/week-6` klasöründen çalıştırılır.

| Dosya | Görevi |
|---|---|
| [pod-nodeselector.yaml](pod-nodeselector.yaml) | `disktype=ssd` etiketli node'a giden Pod |
| [pod-nodeselector-pending.yaml](pod-nodeselector-pending.yaml) | Hiçbir node'da olmayan etiketi isteyen Pod (Pending kalır) |
| [deployment-nodeselector.yaml](deployment-nodeselector.yaml) | 3 replikanın tamamı etiketli node'a toplanır |

## Adım 1: Node etiketlerine bak

```bash
kubectl apply -f Namespace/namespace.yaml
kubectl get nodes --show-labels
```

Her node'da `kubernetes.io/hostname`, `kubernetes.io/arch` gibi hazır etiketler vardır. Özel etiketi biz ekleriz:

```bash
kubectl label node desktop-worker disktype=ssd
kubectl get nodes -L disktype
```

## Adım 2: Etikete uyan Pod

```bash
kubectl apply -f NodeSelector/pod-nodeselector.yaml
kubectl get pod web-ssd -n scheduling-lab -o wide
```

Pod `desktop-worker` node'una düşer.

## Adım 3: Etikete uyan node yok

```bash
kubectl apply -f NodeSelector/pod-nodeselector-pending.yaml
kubectl get pod web-gpu -n scheduling-lab
kubectl describe pod web-gpu -n scheduling-lab
```

Pod `Pending` kalır. `Events` bölümünde sebep yazar:

```
Warning  FailedScheduling  default-scheduler  0/3 nodes are available: 1 node(s) had untolerated taint(s), 2 node(s) didn't match Pod's node affinity/selector.
```

Control-plane taint yüzünden, iki worker ise etiket uyuşmadığı için elenmiştir. Bir node'a `accelerator=gpu` etiketi eklersen Pod otomatik yerleşir.

## Adım 4: Deployment ile

```bash
kubectl apply -f NodeSelector/deployment-nodeselector.yaml
kubectl get pods -n scheduling-lab -l app=web-ssd -o wide
```

3 replikanın hepsi etiketli node'da çalışır. Etiketi kaldırsan bile çalışan Pod'lar silinmez (`IgnoredDuringExecution`), yalnızca yeni Pod'lar yerleşemez.

## nodeSelector sınırları

- Sadece tam eşitlik (`key=value`) destekler. `In`, `NotIn`, `Exists` gibi operatörler ve "tercih edilir ama zorunlu değil" için **Node Affinity** kullanılır.
- Bir node'u Pod'lara **kapatmak** için nodeSelector değil Taint/Toleration kullanılır.

## Temizlik

```bash
kubectl delete -f NodeSelector/
kubectl label node desktop-worker disktype-
```
