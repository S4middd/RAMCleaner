# RAM Cleaner

Pembersih RAM untuk Windows. Jendela utamanya kecil, sisanya hidup di system tray.

Dibuat pakai WinForms + GDI+ saja. Kenapa bukan WPF? Karena runtime WPF (PresentationFramework + compositor) makan 40-60 MB untuk aplikasi yang 99% waktunya cuma tidur di tray. Untuk pembersih RAM, itu ironis. WinForms dengan gambar manual cukup, dan footprint-nya jauh lebih kecil.

Build-nya juga nggak perlu Visual Studio atau SDK. Cukup csc.exe bawaan .NET Framework yang pasti sudah ada di Windows Anda.

## Fitur

- Bersihkan RAM (working set + standby list, perintah yang sama dipakai RAMMap)
- Info pemakaian RAM yang di-refresh tiap 2 detik. Kalau jendela disembunyikan ke tray, refresh dilambatkan jadi 5 detik biar hemat
- Bersihkan RAM langsung dari menu tray, tanpa buka jendela
- Panel alat lanjutan (tersembunyi sampai dibuka): Restart Explorer dan daftar proses lengkap dengan tombol "akhiri proses"
- Single instance. Dijalankan dobel? Nggak bisa, cuma muncul pesan "cek ikonnya di system tray"

## Persyaratan

- Windows 7 sampai 11
- .NET Framework 4.x (Windows 10/11 sudah punya)
- Hak admin, aplikasi otomatis minta lewat UAC

## Cara Build

1. Buka folder project-nya
2. Jalankan `build.bat`
3. Kalau sukses, `RAMCleaner.exe` muncul di folder yang sama

Kalau muncul "GAGAL: csc.exe tidak ditemukan", berarti .NET Framework 4.x belum terpasang.

## Cara Pakai

1. Klik dua kali `RAMCleaner.exe`, klik **Yes** di dialog UAC
2. Klik tombol "Bersihkan RAM" untuk membersihkan, atau klik kanan ikon di tray → "Bersihkan RAM sekarang"
3. Alat lanjutan ada di panel yang dibuka lewat tombol "Tampilkan alat lanjutan ▾" di bawah. Isinya Restart Explorer dan daftar proses (klik kanan proses untuk mengakhirinya)
4. Tutup jendela, aplikasi tetap jalan di tray. Klik dua kali ikonnya untuk buka lagi

## Catatan Jujur Soal Cara Kerjanya

EmptyWorkingSet dan purge standby list TIDAK menghapus data dari RAM. Keduanya cuma memindahkan halaman keluar dari working set proses ke standby list / pagefile.

Angka "Available RAM naik" itu mengukur berapa banyak halaman yang dipindahkan, bukan keuntungan bersih. Aplikasi yang halamannya diambil akan memuat ulang dari disk, dan sesaat terasa lebih lambat. Jadi bersihkan RAM saat benar-benar butuh, bukan tiap lima menit.

## Struktur Project

```
RamCleaner/
├── Program.cs        # Seluruh kode aplikasi (WinForms, C# 5)
├── app.manifest      # Manifest UAC (requireAdministrator) + DPI aware
├── build.bat         # Skrip build (csc.exe)
├── icon.ico          # Ikon aplikasi
└── refs/             # Referensi assembly WPF (untuk build varian lama)
```
