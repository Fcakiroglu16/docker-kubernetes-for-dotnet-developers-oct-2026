# Job

Job, **bir kez çalışıp biten** işleri çalıştıran Kubernetes nesnesidir. Pod'un sürekli ayakta kalması beklenir. Job'un Pod'u ise işini bitirince `Completed` olur ve durur. Hata verirse Job yeniden dener. Tipik kullanım: veritabanı migration'ı, rapor üretme, veri aktarımı, backup.

Bu dokümanda 5 örnek var. Her biri Job'un farklı bir yönünü gösterir:

| Dosya | Gösterdiği |
|---|---|
| [job-migration.yaml](job-migration.yaml) | Tek seferlik iş (migration örneği). `backoffLimit`, `activeDeadlineSeconds`, `ttlSecondsAfterFinished` |
| [job-completions.yaml](job-completions.yaml) | `completions: 5`, `parallelism: 1`. Pod'lar **sırayla** çalışır |
| [job-parallel.yaml](job-parallel.yaml) | `completions: 6`, `parallelism: 3`. Pod'lar **3'erli** çalışır |
| [job-indexed.yaml](job-indexed.yaml) | `completionMode: Indexed`. Her Pod'a kendi numarası verilir |
| [job-backoff.yaml](job-backoff.yaml) | Sürekli hata veren iş. `backoffLimit` aşılınca Job `Failed` olur |

Zamanlanmış (periyodik) çalışan işler için bkz. [cronjob.md](../CronJob/cronjob.md).

## Hazırlık

Örnekler `busybox:1.37` image'ını kullanır, projeyi build etmek gerekmez. Gerçek bir projede container'ın içinde migration aracı ya da uygulamanın kendisi çalışır. Burada iş, `sh -c` ile çalışan küçük bir script ile **simüle edilir**. `command: ["/bin/sh", "-c"]` shell'i başlatır, `args` altındaki metin ise shell'e verilen script'tir.

`ecommerce-dev` namespace'inde ResourceQuota ve LimitRange vardır (bkz. [namespace.md](../namespace/namespace.md)). LimitRange her container için en az `50m` CPU ve `64Mi` memory ister. Bu yüzden YAML'larda `resources` açıkça yazılmıştır. Ayrıca `count/jobs.batch` kotası namespace'te en fazla 10 Job'a izin verir. Örnekleri bitirdikçe temizlemek iyi olur (sonda temizlik var).

Tüm komutlar repo kök klasöründen çalıştırılır.

## Örnek 1: Migration Job

Dosya: [job-migration.yaml](job-migration.yaml)

```bash
kubectl apply -f kubernetes/week-3/Job/job-migration.yaml
```

Job'un bitmesini bekle (yaklaşık 10 sn):

```bash
kubectl wait --for=condition=Complete job/db-migration -n ecommerce-dev --timeout=90s
```

Job'u ve Pod'unu göster. `COMPLETIONS 1/1` ve Pod `Completed` olmalı:

```bash
kubectl get job db-migration -n ecommerce-dev
kubectl get pods -n ecommerce-dev -l job-type=migration
```

Job'un Pod'una gitmeden log'u al:

```bash
kubectl logs job/db-migration -n ecommerce-dev
```

Beklenen çıktı:

```text
[1/3] Migration basliyor: veritabani semasi kontrol ediliyor...
[2/3] 20261020_AddProductTable uygulaniyor...
[3/3] 20261021_AddOrderTable uygulaniyor...
Migration tamamlandi.
```

YAML'daki önemli alanlar:

| Alan | Anlamı |
|---|---|
| `backoffLimit: 3` | Hata olursa en fazla 3 kez yeniden dene. Varsayılan 6'dır |
| `activeDeadlineSeconds: 120` | Job 120 sn içinde bitmezse zorla durdurulur ve `Failed` olur. Takılan bir migration sonsuza kadar sürmesin diye |
| `ttlSecondsAfterFinished: 600` | Job bittikten 10 dk sonra Job ve Pod'ları otomatik silinir. Yoksa biten Job'lar birikir |
| `restartPolicy: OnFailure` | Hata olursa container aynı Pod içinde yeniden başlatılır. Job Pod'unda yalnızca `Never` ya da `OnFailure` olabilir |

> Aynı isimli bir Job bitmiş olsa bile tekrar `apply` edilemez, çünkü Job'un `template` alanı sonradan değiştirilemez. Yeni bir migration çalıştırmak için önce eski Job silinir ya da yeni bir isim verilir.

## Örnek 2: `completions` ve sıralı çalışma

Dosya: [job-completions.yaml](job-completions.yaml)

`completions: 5` "bu işin başarıyla 5 kez bitmesi gerekir" demektir. `parallelism: 1` olduğu için bir Pod bitmeden diğeri başlamaz.

```bash
kubectl apply -f kubernetes/week-3/Job/job-completions.yaml
```

Birkaç saniye sonra bak. Tek bir Pod çalışır, biten Pod'lar `Completed` olarak kalır ve `COMPLETIONS` sayısı artar:

```bash
sleep 7
kubectl get pods -n ecommerce-dev -l job-type=completions-demo
kubectl get job job-completions -n ecommerce-dev
```

Job'un bitmesini bekle (yaklaşık 30 sn) ve sonucu gör. `AGE` sütunundaki farklara bak: Pod'lar 6'şar sn arayla başlamıştır:

```bash
kubectl wait --for=condition=Complete job/job-completions -n ecommerce-dev --timeout=120s
kubectl get pods -n ecommerce-dev -l job-type=completions-demo
kubectl get job job-completions -n ecommerce-dev
```

Beklenen son durum:

```text
NAME              STATUS     COMPLETIONS   DURATION   AGE
job-completions   Complete   5/5           30s        30s
```

## Örnek 3: `parallelism` ve paralel çalışma

Dosya: [job-parallel.yaml](job-parallel.yaml)

`completions: 6` ve `parallelism: 3`. Aynı anda 3 Pod çalışır. İlk üçü bitince sıradaki üçü başlar:

```bash
kubectl apply -f kubernetes/week-3/Job/job-parallel.yaml
```

Hemen ardından bak. Aynı anda **3 Pod `Running`** olmalı:

```bash
sleep 4
kubectl get pods -n ecommerce-dev -l job-type=parallel-demo
```

İlk dalga bitip ikinci dalga başlayınca (yaklaşık 6 sn sonra) tekrar bak. İlk üç Pod `Completed`, yeni üç Pod `Running` olmalı:

```bash
sleep 6
kubectl get pods -n ecommerce-dev -l job-type=parallel-demo
```

```bash
kubectl wait --for=condition=Complete job/job-parallel -n ecommerce-dev --timeout=120s
kubectl get job job-parallel -n ecommerce-dev
```

Sıralı (Örnek 2) ile paralel (Örnek 3) karşılaştırması:

| | completions | parallelism | Davranış |
|---|---|---|---|
| Tek iş | (yok, 1) | (yok, 1) | Tek Pod, bir kez |
| Sıralı | 5 | 1 | 5 Pod, biri bitince diğeri başlar |
| Paralel | 6 | 3 | 6 Pod, aynı anda en fazla 3'ü |
| Kuyruk (work queue) | (yok) | 3 | Pod'lar kuyruktan iş çeker, biri başarıyla bitince diğerleri de biter. Bu örnekte yok |

> Paralel Pod'lar kotadan da harcar. 3 Pod x `100m` CPU = `300m` istek demektir. `parallelism` değerini kotayı aşacak kadar büyük verirsen fazla Pod'lar oluşturulamaz.

## Örnek 4: Indexed Job

Dosya: [job-indexed.yaml](job-indexed.yaml)

`completionMode: Indexed` ile her Pod'a `0` ile `completions-1` arasında benzersiz bir numara verilir. Numara container içinde `JOB_COMPLETION_INDEX` ortam değişkeni olarak görünür ve Pod adına eklenir. Büyük bir veriyi parçalara bölüp her Pod'un kendi parçasını işlemesi için kullanılır (örneğin 0. Pod ilk 1000 kaydı, 1. Pod sonraki 1000 kaydı).

```bash
kubectl apply -f kubernetes/week-3/Job/job-indexed.yaml
kubectl wait --for=condition=Complete job/job-indexed -n ecommerce-dev --timeout=120s
```

Pod adlarının ortasındaki `0`, `1`, `2` numaralara dikkat et:

```bash
kubectl get pods -n ecommerce-dev -l job-type=indexed-demo
```

Her Pod kendi numarasını yazdırır:

```bash
kubectl logs -n ecommerce-dev -l job-type=indexed-demo --prefix
```

Her Pod için iki satır olmalı: `Parca 0 isleniyor (toplam 3 parca)...` ve `Parca 0 tamamlandi.` (1 ve 2 için de aynısı).

## Örnek 5: Başarısız Job ve `backoffLimit`

Dosya: [job-backoff.yaml](job-backoff.yaml)

Container her seferinde `exit 1` ile hata kodu döndürür. `backoffLimit: 2` olduğu için ilk deneme ve 2 yeniden deneme olmak üzere toplam **3 Pod** oluşur. Sonra Job `Failed` olur. Denemeler arasında bekleme süresi katlanarak artar (10 sn, 20 sn, 40 sn...).

```bash
kubectl apply -f kubernetes/week-3/Job/job-backoff.yaml
```

Job'un `Failed` olmasını bekle (yaklaşık 30 sn):

```bash
kubectl wait --for=condition=Failed job/job-backoff -n ecommerce-dev --timeout=150s
```

Üç Pod'un da `Error` durumunda olduğuna bak. `restartPolicy: Never` olduğu için her denemede **yeni Pod** oluştu, `RESTARTS` hep 0:

```bash
kubectl get job job-backoff -n ecommerce-dev
kubectl get pods -n ecommerce-dev -l job-type=failure-demo
```

Neden başarısız olduğunu Job'un olaylarından (events) oku. Sonda `BackoffLimitExceeded` görünür:

```bash
kubectl describe job job-backoff -n ecommerce-dev | tail -6
```

`restartPolicy` farkı:

| `restartPolicy` | Hata olunca |
|---|---|
| `Never` | Container yeniden başlatılmaz, Job **yeni bir Pod** oluşturur. Her denemenin Pod'u ve log'u ayrı kalır. Hata ayıklamak için kolaydır |
| `OnFailure` | **Aynı Pod** içinde container yeniden başlatılır (`RESTARTS` artar). Pod sayısı artmaz |

## Notlar

- Job bittiğinde Pod'lar silinmez, `Completed` olarak kalır ve log'ları okunabilir. Silinmeleri için Job'u silmek ya da `ttlSecondsAfterFinished` kullanmak gerekir.
- `kubectl delete job` Job'un Pod'larını da siler.
- Job'lar **en az bir kez** (at-least-once) çalışır. Nadir durumlarda aynı iş iki kez çalışabilir, bu yüzden migration ve backup gibi işler tekrar çalışınca zarar vermeyecek şekilde (idempotent) yazılmalıdır.
- Gerçek bir EF Core migration'ı için genelde `dotnet ef migrations bundle` ile tek dosyalık bir çalıştırılabilir üretilir ve bu Job'un image'ına konur. Bu repodaki örnek simülasyondur, gerçek bir veritabanına karşı denenmedi.
- `activeDeadlineSeconds` YAML'da vardır ama süreyi aşan bir durum bu dokümanda denenmedi. Bilgi Kubernetes dokümanındandır.

## Temizle

```bash
kubectl delete job db-migration job-completions job-parallel job-indexed job-backoff -n ecommerce-dev
```
