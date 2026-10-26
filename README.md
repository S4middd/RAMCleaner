# RAM Cleaner

Aplikasi pembersih memori untuk Windows. Ringan, berjalan di system tray, dibangun dengan WinForms + GDI+ tanpa dependensi eksternal (tidak butuh .NET SDK, cukup csc.exe bawaan .NET Framework).

## Fitur

- **Bersihkan RAM** — mengosongkan working set proses dan purge standby list (perintah yang sama dipakai RAMMap).
- **Monitoring realtime** — tampilan penggunaan RAM diperbarui tiap 2 detik (melambat jadi 5 detik saat jendela disembunyikan di tray).
- **System tray** — bersihkan RAM langsung dari menu tray tanpa membuka jendela.
- **Alat lanjutan** (panel tersembunyi):
  - Restart Explorer
  - Daftar proses + akhiri proses tertentu
- Single instance — tidak bisa dijalankan dobel.
- Footprint kecil: tanpa WPF/runtime berat, 99% waktunya tidur di tray.

## Persyaratan

- Windows 7 / 8 / 8.1 / 10 / 11
- .NET Framework 4.x (sudah bawaan Windows 10/11)
- Hak administrator (aplikasi otomatis minta UAC via manifest `requireAdministrator`)

## Langkah Build

1. Buka folder project.
2. Jalankan `build.bat` (klik dua kali atau dari command prompt).
3. Jika sukses, muncul `RAMCleaner.exe` di folder yang sama.

Build memakai `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` — tidak perlu Visual Studio atau SDK apa pun.

## Langkah Menjalankan

1. Klik dua kali `RAMCleaner.exe`.
2. Klik **Yes** pada dialog UAC (aplikasi butuh hak admin untuk membersihkan memori).
3. Jendela utama terbuka, ikon muncul di system tray.

## Langkah Penggunaan

1. **Bersihkan RAM** — klik tombol biru "Bersihkan RAM", atau klik kanan ikon tray → "Bersihkan RAM sekarang".
2. **Alat lanjutan** — klik "Tampilkan alat lanjutan ▾" untuk membuka panel: Restart Explorer, daftar proses, akhiri proses.
3. **Sembunyikan ke tray** — tutup/minimize jendela; aplikasi tetap berjalan di tray. Klik dua kali ikon tray untuk membuka lagi.

## Cara Kerja & Catatan Jujur

`EmptyWorkingSet` dan purge standby list **tidak menghapus data dari RAM**. Keduanya memindahkan halaman keluar dari working set proses ke standby list / pagefile. Angka "Available RAM naik" mengukur berapa banyak halaman yang dipindahkan, bukan keuntungan bersih — aplikasi yang halamannya diambil akan memuat ulang dari disk dan sesaat terasa lebih lambat.

## Struktur Project

```
RamCleaner/
├── Program.cs        # Seluruh kode aplikasi (WinForms, C# 5)
├── app.manifest      # Manifest UAC (requireAdministrator) + DPI aware
├── build.bat         # Skrip build (csc.exe)
├── icon.ico          # Ikon aplikasi
└── refs/             # Referensi assembly WPF (untuk build varian lama)
```
