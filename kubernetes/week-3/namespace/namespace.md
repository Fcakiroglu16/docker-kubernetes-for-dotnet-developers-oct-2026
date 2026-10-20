# Namespace

Tüm komutlar repo kök klasöründen çalıştırılır.

## Mevcut namespace'leri listele

```bash
kubectl get namespaces
```

## Komutla namespace oluştur

```bash
kubectl create namespace ecommerce-dev
```

## YAML ile namespace oluştur (label'larla)

`environment` ve `team` label'ları namespace'i filtrelemek ve gruplamak için kullanılır. Dosya: [namespace.yaml](namespace.yaml)

```bash
kubectl apply -f kubernetes/week-3/namespace/namespace.yaml
```

## Namespace'e kaynak limiti (ResourceQuota) koy

Namespace içindeki toplam kaynak kullanımını sınırlar: compute (CPU/memory), storage, ve nesne sayıları (pod, service, configmap, secret vb.). Dosya: [resourcequota.yaml](resourcequota.yaml)

```bash
kubectl apply -f kubernetes/week-3/namespace/resourcequota.yaml
```

> `configmaps` ve `secrets` quota'sı yalnızca adet sınırlar. Her namespace'te Kubernetes'in oluşturduğu `kube-root-ca.crt` ConfigMap'i de bu sayıya dahildir.

## Namespace'e varsayılan ve sınır değerleri (LimitRange) koy

ResourceQuota toplamı sınırlar, LimitRange ise tek bir container, pod veya PVC için varsayılan, minimum ve maksimum değerleri belirler. Namespace'te CPU/memory quota'sı varsa her container'ın requests/limits değeri olmak zorundadır. LimitRange bu değerleri belirtmeyen container'lara otomatik atar. Dosya: [limitrange.yaml](limitrange.yaml)

```bash
kubectl apply -f kubernetes/week-3/namespace/limitrange.yaml
```

## ConfigMap ve Secret sayı limitini test et

Limit dolunca yeni nesne oluşturulamaz.

```bash
kubectl create configmap app-config --from-literal=LOG_LEVEL=Information -n ecommerce-dev
kubectl create secret generic app-secret --from-literal=DB_PASSWORD=P@ssw0rd -n ecommerce-dev
```

## Quota ve LimitRange durumunu göster

`Used` ve `Hard` sütunları ile hangi kaynaktan ne kadar kullanıldığını gösterir.

```bash
kubectl get resourcequota -n ecommerce-dev
kubectl describe resourcequota ecommerce-dev-quota -n ecommerce-dev
kubectl get limitrange -n ecommerce-dev
kubectl describe limitrange ecommerce-dev-limits -n ecommerce-dev
```

## Namespace detayını ve quota kullanımını göster

```bash
kubectl describe namespace ecommerce-dev
```

## Namespace'i sil

Namespace içindeki tüm kaynaklar da birlikte silinir.

```bash
kubectl delete namespace ecommerce-dev
```
