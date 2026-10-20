# CronJob

CronJob, bir [Job](../Job/job.md)'u **zamanlanmış** olarak tekrar tekrar oluşturur. Linux'taki `cron` ile aynı mantıktır. Her zamanlamada CronJob yeni bir Job oluşturur, Job da Pod'u çalıştırır. Tipik kullanım: gece backup'ı, periyodik temizlik, günlük rapor.

Bu dokümandaki örnek bir **backup** işidir. İş çalışınca log'a `Backup alindi: ...` yazar. Gerçek bir yedek alınmaz, simülasyondur.

| Dosya | İçeriği |
|---|---|
| [cronjob-backup.yaml](cronjob-backup.yaml) | Her dakika çalışan `db-backup` CronJob'u (`schedule`, `timeZone`, `concurrencyPolicy`, geçmiş limitleri, `suspend`) |
| [cronjob-allow.yaml](cronjob-allow.yaml) | `concurrencyPolicy: Allow`. İşler üst üste çalışır |
| [cronjob-forbid.yaml](cronjob-forbid.yaml) | `concurrencyPolicy: Forbid`. Önceki iş sürerken yeni çalıştırma atlanır |
| [cronjob-replace.yaml](cronjob-replace.yaml) | `concurrencyPolicy: Replace`. Önceki iş silinir, yenisi başlar |
| [cronjob-parallel.yaml](cronjob-parallel.yaml) | `jobTemplate` içinde `completions`/`parallelism`/`Indexed`. Her tetiklenmede 3 paralel Pod |
| [cronjob-failing.yaml](cronjob-failing.yaml) | Sürekli hata veren CronJob. `failedJobsHistoryLimit` ile hatalı Job'ların birikmemesi |

Job kavramları (`backoffLimit`, `restartPolicy`, `activeDeadlineSeconds` vb.) [job.md](../Job/job.md) dosyasında anlatılmıştır. Tüm komutlar repo kök klasöründen çalıştırılır.

## Cron formatı

`schedule` alanı 5 parçadan oluşur:

```text
┌───────── dakika (0-59)
│ ┌─────── saat (0-23)
│ │ ┌───── ayın günü (1-31)
│ │ │ ┌─── ay (1-12)
│ │ │ │ ┌─ haftanın günü (0-6, 0 = Pazar)
* * * * *
```

| `schedule` | Anlamı |
|---|---|
| `* * * * *` | Her dakika (bu örnekte, sonucu hemen görmek için) |
| `*/5 * * * *` | 5 dakikada bir |
| `0 2 * * *` | Her gece 02:00 (tipik backup zamanı) |
| `0 */6 * * *` | 6 saatte bir |
| `30 8 * * 1-5` | Hafta içi her gün 08:30 |
| `0 0 1 * *` | Her ayın 1'i gece yarısı |

## Adım 1: CronJob'u oluştur

```bash
kubectl apply -f kubernetes/week-3/CronJob/cronjob-backup.yaml
kubectl get cronjob db-backup -n ecommerce-dev
```

İlk anda `LAST SCHEDULE` boştur (`<none>`), çünkü henüz zamanı gelmedi.

## Adım 2: İlk çalıştırmayı bekle

Zamanlama her dakika başı olduğu için en fazla 1 dakika bekle:

```bash
sleep 70
kubectl get cronjob db-backup -n ecommerce-dev
kubectl get jobs -n ecommerce-dev -l job-type=backup
kubectl get pods -n ecommerce-dev -l job-type=backup
```

`LAST SCHEDULE` dolmuş, bir Job oluşmuş ve Pod'u `Completed` olmalı. CronJob'un oluşturduğu Job'un adı `db-backup-<zaman damgası>` biçimindedir.

## Adım 3: Backup log'unu oku

```bash
kubectl logs -n ecommerce-dev -l job-type=backup --prefix
```

Beklenen çıktı:

```text
[pod/db-backup-29875061-hv9nk/backup] Backup basliyor: 2026-10-20 13:41:00
[pod/db-backup-29875061-hv9nk/backup] Backup alindi: ecommerce-db-20261020-134103.bak
```

> Container içindeki `date` **UTC** gösterir (yukarıda 13:41, Türkiye saati 16:41). `timeZone: "Europe/Istanbul"` yalnızca CronJob'un ne zaman tetikleneceğini belirler, container'ın saatini değiştirmez.

## Adım 4: CronJob'u elle tetikle

Zamanını beklemeden CronJob'un şablonundan bir Job oluştur. Backup'ı hemen almak ya da CronJob'u denemek için kullanışlıdır:

```bash
kubectl create job db-backup-manual --from=cronjob/db-backup -n ecommerce-dev
kubectl wait --for=condition=Complete job/db-backup-manual -n ecommerce-dev --timeout=60s
kubectl logs job/db-backup-manual -n ecommerce-dev
```

## Adım 5: CronJob'u duraklat ve devam ettir

`suspend: true` yeni Job oluşturulmasını durdurur. CronJob silinmez, çalışan Job'lar etkilenmez:

```bash
kubectl patch cronjob db-backup -n ecommerce-dev -p '{"spec":{"suspend":true}}'
kubectl get cronjob db-backup -n ecommerce-dev
```

`SUSPEND` sütunu `True` olmalı. Bakım sırasında backup'ın çalışmasını istemediğinde kullanılır. Duraklatılmışken Job sayısının artmadığını görmek için 70 sn bekleyip say:

```bash
kubectl get jobs -n ecommerce-dev -l job-type=backup --no-headers | wc -l
sleep 70
kubectl get jobs -n ecommerce-dev -l job-type=backup --no-headers | wc -l
```

İki sayı aynı olmalı. Devam ettir:

```bash
kubectl patch cronjob db-backup -n ecommerce-dev -p '{"spec":{"suspend":false}}'
```

## Adım 6: Zamanlamayı değiştir

Yaml'ı düzenlemeden `patch` ile zamanlama değiştirilebilir. 5 dakikada bire al:

```bash
kubectl patch cronjob db-backup -n ecommerce-dev -p '{"spec":{"schedule":"*/5 * * * *"}}'
kubectl get cronjob db-backup -n ecommerce-dev
```

`SCHEDULE` sütunu `*/5 * * * *` olmalı. `kubectl apply -f` ile yaml yeniden uygulanırsa zamanlama tekrar `* * * * *` olur.

## Geçmiş limitleri

`successfulJobsHistoryLimit: 3` ve `failedJobsHistoryLimit: 1` ayarı sayesinde CronJob eski Job'ları kendisi siler. Yoksa her dakika bir Job birikir ve `count/jobs.batch` kotası dolar. Birkaç çalıştırmadan sonra başarılı Job sayısının 3'ü geçmediğini görürsün:

```bash
kubectl get jobs -n ecommerce-dev -l job-type=backup
```

> `kubectl create job --from=cronjob/...` ile elle tetiklenen Job da CronJob'a bağlıdır ve bu limite **sayılır**. En eskisi silindiği için Adım 4'te oluşturduğun `db-backup-manual` Job'u birkaç dakika sonra kendiliğinden kaybolabilir.

## Örnek 2: `concurrencyPolicy` (üç politika)

Önceki Job hâlâ çalışırken yeni zaman gelirse ne olacağını `concurrencyPolicy` belirler. Üç örnekte de CronJob **her dakika** tetiklenir ama iş **150 sn** sürer. Yani her tetiklenmede bir önceki iş henüz bitmemiştir.

| Değer | Davranış |
|---|---|
| `Allow` | (varsayılan) Yeni Job başlar, işler **üst üste** çalışır |
| `Forbid` | Yeni çalıştırma **atlanır**. Backup ve migration gibi çakışmaması gereken işler için |
| `Replace` | Çalışan Job **silinir**, yerine yenisi başlar |

> **Bu üç örneği ve diğerlerini aynı anda çalıştırma.** Her CronJob birkaç Job üretir ve `ecommerce-dev` kotası en fazla 10 Job'a izin verir. Beş CronJob'u birlikte çalıştırdığımızda kota doldu ve CronJob'lar `Error creating job: ... exceeded quota: count/jobs.batch` hatası verdi. Her örnek kendi temizliğiyle bitiyor, sırayla çalıştır. Başlamadan önce Adım 1-6'daki `db-backup` CronJob'unu sil.

Önce önceki bölümlerden kalan CronJob'u sil:

```bash
kubectl delete cronjob db-backup -n ecommerce-dev --ignore-not-found
```

### `Allow`: işler üst üste biner

Dosya: [cronjob-allow.yaml](cronjob-allow.yaml)

```bash
kubectl apply -f kubernetes/week-3/CronJob/cronjob-allow.yaml
```

İkinci tetiklenmeden sonra (yaklaşık 2 dakika) bak. `ACTIVE` sütunu **1'den büyük (2 ya da 3)** olmalı: ilk iş bitmeden yenileri başlamıştır. Tetiklenme zamanına göre değişir. Pod listesinde birden fazla Pod aynı anda `Running` görünür:

```bash
sleep 135
kubectl get cronjob cron-allow -n ecommerce-dev
kubectl get pods -n ecommerce-dev -l job-type=concurrency-allow
```

```bash
kubectl delete cronjob cron-allow -n ecommerce-dev
```

### `Forbid`: yeni çalıştırma atlanır

Dosya: [cronjob-forbid.yaml](cronjob-forbid.yaml)

```bash
kubectl apply -f kubernetes/week-3/CronJob/cronjob-forbid.yaml
```

Bu sefer `ACTIVE` hep **1** kalır, ikinci Job oluşturulmaz. CronJob'un olaylarında (events) atlama nedeni görünür:

```bash
sleep 140
kubectl get cronjob cron-forbid -n ecommerce-dev
kubectl describe cronjob cron-forbid -n ecommerce-dev | tail -4
```

Beklenen olay satırı:

```text
Normal  JobAlreadyActive  ...  cronjob-controller  Not starting job because prior execution is running and concurrency policy is Forbid
```

```bash
kubectl delete cronjob cron-forbid -n ecommerce-dev
```

### `Replace`: eski iş silinir

Dosya: [cronjob-replace.yaml](cronjob-replace.yaml)

```bash
kubectl apply -f kubernetes/week-3/CronJob/cronjob-replace.yaml
```

İkinci tetiklenmede çalışan Job silinir ve yenisi başlar. Aynı anda hep **tek** Job vardır ve olaylarda `SuccessfulDelete` görünür:

```bash
sleep 140
kubectl get jobs -n ecommerce-dev -l job-type=concurrency-replace
kubectl describe cronjob cron-replace -n ecommerce-dev | tail -5
```

> `Replace` ile silinen iş 150 sn'lik işini bitiremeden öldürülür. İş yarım kalmaması gerekiyorsa `Forbid` seç.

```bash
kubectl delete cronjob cron-replace -n ecommerce-dev
```

## Örnek 3: CronJob içinde paralel Job

Dosya: [cronjob-parallel.yaml](cronjob-parallel.yaml)

`completions`, `parallelism` ve `completionMode` gibi Job alanları CronJob'da `jobTemplate.spec` altına yazılır ([job.md](../Job/job.md) Örnek 3 ve 4). Bu örnek **2 dakikada bir** çalışır ve her tetiklenmede 3 Pod'u paralel çalıştırıp bir "rapor" üretir (Indexed, her Pod kendi parçasını):

```bash
kubectl apply -f kubernetes/week-3/CronJob/cronjob-parallel.yaml
```

Bir sonraki çift dakika başında Job oluşur (en fazla 2 dakika bekle):

```bash
sleep 135
kubectl get jobs -n ecommerce-dev -l job-type=cron-parallel
kubectl get pods -n ecommerce-dev -l job-type=cron-parallel
kubectl logs -n ecommerce-dev -l job-type=cron-parallel --prefix
```

Bir tetiklenme için **3 Pod** (`-0-`, `-1-`, `-2-`) ve `COMPLETIONS 3/3` görmelisin.

```bash
kubectl delete cronjob cron-parallel -n ecommerce-dev
```

## Örnek 4: Sürekli hata veren CronJob

Dosya: [cronjob-failing.yaml](cronjob-failing.yaml)

Her dakika çalışan ve her seferinde `exit 1` ile biten bir iş. `failedJobsHistoryLimit: 1` olduğu için hatalı Job'lardan sadece **en sonuncusu** saklanır. Yoksa dakikada bir Job birikir ve kota dolar:

```bash
kubectl apply -f kubernetes/week-3/CronJob/cronjob-failing.yaml
```

Üç tetiklenmeden sonra bak:

```bash
sleep 170
kubectl get jobs -n ecommerce-dev -l job-type=cron-failing
kubectl logs -n ecommerce-dev -l job-type=cron-failing --prefix
```

Üç Job çalışmış olmasına rağmen listede **tek** `Failed` Job görünür ve log'unda hata mesajı vardır. Hata ayıklamak için bu son Job'un log'u okunur.

```bash
kubectl delete cronjob cron-failing -n ecommerce-dev
```

## Notlar

- CronJob de bir Job oluşturduğu için Job'un tüm alanları (`backoffLimit`, `ttlSecondsAfterFinished` vb.) `jobTemplate.spec` altında kullanılabilir (bkz. Örnek 3).
- `ecommerce-dev` kotası en fazla 5 CronJob ve 10 Job'a izin verir (bkz. [namespace.md](../namespace/namespace.md)). Bu yüzden geçmiş limitleri düşük tutulmuştur.
- CronJob'u silmek (`kubectl delete cronjob`) onun oluşturduğu Job ve Pod'ları da siler.
- Zamanlama kaçırılabilir (controller kapalıydı, `suspend` açıktı). `startingDeadlineSeconds` kaçan çalıştırmanın ne kadar geç hâlâ başlatılabileceğini belirler.
- Gerçek bir backup için yedeğin Pod silinince kaybolmaması gerekir. Yedek bir PersistentVolume'a ya da bulut depolamaya (S3, Azure Blob) yazılmalıdır. Bu örnek yalnızca zamanlama mantığını gösterir.

## Temizle

```bash
kubectl delete cronjob db-backup -n ecommerce-dev --ignore-not-found
kubectl delete job db-backup-manual -n ecommerce-dev --ignore-not-found
```
