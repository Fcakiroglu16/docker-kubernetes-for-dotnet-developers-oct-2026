# DaemonSet

DaemonSet, her node'da (veya seçilen node'larda) **tam 1 Pod** çalıştırır. Cluster'a yeni node eklenince Pod otomatik oraya da açılır, node silinince Pod da silinir. Replika sayısı yazılmaz, sayıyı node sayısı belirler.

Tipik kullanım: log toplayıcı (Fluent Bit), metrik toplayıcı (node-exporter), ağ eklentisi (kindnet, kube-proxy).

Komutlar `kubernetes/week-6` klasöründen çalıştırılır.

| Dosya | Görevi |
|---|---|
| [../Namespace/namespace.yaml](../Namespace/namespace.yaml) | `scheduling-lab` namespace'i |
| [log-agent-daemonset.yaml](log-agent-daemonset.yaml) | Tüm node'larda çalışan DaemonSet (control-plane dahil) |
| [log-agent-ssd-daemonset.yaml](log-agent-ssd-daemonset.yaml) | Sadece `disktype=ssd` etiketli node'larda çalışan DaemonSet |

## Adım 1: Namespace ve DaemonSet

```bash
kubectl apply -f Namespace/namespace.yaml
kubectl apply -f DaemonSet/log-agent-daemonset.yaml
kubectl rollout status ds/log-agent -n scheduling-lab
kubectl get pods -n scheduling-lab -o wide
```

3 node'lu kind cluster'da 3 Pod görünür, her node'da bir tane:

```
log-agent-7fkqs   1/1   Running   10.244.0.6    desktop-control-plane
log-agent-xgc57   1/1   Running   10.244.2.10   desktop-worker
log-agent-xz2r7   1/1   Running   10.244.1.8    desktop-worker2
```

Control-plane node'da Pod'un çalışmasını `tolerations` sağlar. Bu bloğu silersen control-plane'e Pod düşmez, çünkü o node `node-role.kubernetes.io/control-plane:NoSchedule` taint'i taşır. Pod, hangi node'da çalıştığını Downward API ile (`spec.nodeName`) öğrenir:

```bash
kubectl logs -n scheduling-lab ds/log-agent --tail=2
```

## Adım 2: Sadece belirli node'larda çalıştırma

`log-agent-ssd` DaemonSet'i `nodeSelector: disktype=ssd` kullanır. Etiketli node yokken hiç Pod açılmaz:

```bash
kubectl apply -f DaemonSet/log-agent-ssd-daemonset.yaml
kubectl get ds log-agent-ssd -n scheduling-lab
```

`DESIRED` değeri `0` olur. Bir node'u etiketle, Pod birkaç saniyede o node'da açılır:

```bash
kubectl label node desktop-worker disktype=ssd
kubectl get pods -n scheduling-lab -l app=log-agent-ssd -o wide
```

Etiketi kaldırınca Pod otomatik silinir:

```bash
kubectl label node desktop-worker disktype-
```

## Adım 3: Rolling update

```bash
kubectl set image ds/log-agent agent=busybox:1.37 -n scheduling-lab
kubectl rollout status ds/log-agent -n scheduling-lab --timeout=180s
```

`maxUnavailable: 1` olduğu için node'lar tek tek güncellenir. İmaj her node'da ayrı çekildiği için kind'da birkaç dakika sürebilir. Geri almak için:

```bash
kubectl rollout undo ds/log-agent -n scheduling-lab
```

> `updateStrategy.type: OnDelete` seçilirse Pod'lar sen elle silene kadar güncellenmez.

## Temizlik

```bash
kubectl delete -f DaemonSet/log-agent-daemonset.yaml
kubectl delete -f DaemonSet/log-agent-ssd-daemonset.yaml --ignore-not-found
kubectl label node desktop-worker disktype- --ignore-not-found
```
