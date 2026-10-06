Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks

Namespace Stok

    ''' <summary>
    ''' Örnek plugin (VB.NET) - Stok servisi, SÜRÜM 2. v1'e göre yenilikler:
    '''   - constructor'a Optional kritikSeviye eklendi (v1 kaydı constructorArgs değişmeden v2'ye geçebilir),
    '''   - KritikSeviye (okunur/yazılır), KritikSeviyeAltinda event'i,
    '''   - Async SayimAsync (depolar paralel sayılır - Task.WhenAll), Transfer, ParamArray ile ToplamMiktar.
    ''' </summary>
    Public Class StokServisi

        Private ReadOnly _kalemler As New Dictionary(Of String, StokKalemi)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _kilit As New Object()
        Private _kritikSeviye As Integer

        Public Event KritikSeviyeAltinda As EventHandler(Of KritikStokEventArgs)

        Public Sub New(Optional varsayilanDepo As String = "ANA", Optional kritikSeviye As Integer = 10)
            Me.VarsayilanDepo = varsayilanDepo
            Me.KritikSeviye = kritikSeviye
            Giris("KLM-001", 120)
            Giris("KLM-002", 40)
            Giris("DFT-010", 15, "SUBE1")
            Giris("KLM-001", 30, "SUBE1")
        End Sub

        Public ReadOnly Property Surum As String
            Get
                Return "2.0"
            End Get
        End Property

        Public ReadOnly Property VarsayilanDepo As String

        Public Property KritikSeviye As Integer
            Get
                Return _kritikSeviye
            End Get
            Set(value As Integer)
                If value < 0 Then Throw New ArgumentOutOfRangeException(NameOf(value), "Kritik seviye negatif olamaz.")
                _kritikSeviye = value
            End Set
        End Property

        Public ReadOnly Property KalemSayisi As Integer
            Get
                SyncLock _kilit
                    Return _kalemler.Count
                End SyncLock
            End Get
        End Property

        Public Sub Giris(urunKodu As String, adet As Integer, Optional depo As String = Nothing)
            If String.IsNullOrWhiteSpace(urunKodu) Then Throw New ArgumentException("Ürün kodu boş olamaz.", NameOf(urunKodu))
            If adet <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(adet), "Adet pozitif olmalı.")
            Dim d = If(depo, VarsayilanDepo)
            SyncLock _kilit
                Dim k As StokKalemi = Nothing
                If Not _kalemler.TryGetValue(Anahtar(urunKodu, d), k) Then
                    k = New StokKalemi With {.UrunKodu = urunKodu.ToUpperInvariant(), .Depo = d.ToUpperInvariant()}
                    _kalemler(Anahtar(urunKodu, d)) = k
                End If
                k.Miktar += adet
                k.SonHareket = DateTime.Now
            End SyncLock
        End Sub

        Public Function Cikis(urunKodu As String, adet As Integer, Optional depo As String = Nothing) As Boolean
            If adet <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(adet), "Adet pozitif olmalı.")
            Dim kalan As Integer
            Dim d = If(depo, VarsayilanDepo)
            SyncLock _kilit
                Dim k As StokKalemi = Nothing
                If Not _kalemler.TryGetValue(Anahtar(urunKodu, d), k) OrElse k.Miktar < adet Then Return False
                k.Miktar -= adet
                k.SonHareket = DateTime.Now
                kalan = k.Miktar
            End SyncLock
            If kalan < KritikSeviye Then
                RaiseEvent KritikSeviyeAltinda(Me, New KritikStokEventArgs With {.UrunKodu = urunKodu.ToUpperInvariant(), .Depo = d, .Kalan = kalan, .Kritik = KritikSeviye})
            End If
            Return True
        End Function

        Public Function Miktar(urunKodu As String, Optional depo As String = Nothing) As Integer
            SyncLock _kilit
                Dim k As StokKalemi = Nothing
                Return If(_kalemler.TryGetValue(Anahtar(urunKodu, If(depo, VarsayilanDepo)), k), k.Miktar, 0)
            End SyncLock
        End Function

        ''' <summary>Birden çok ürünün tüm depolardaki toplamı: ToplamMiktar("KLM-001", "KLM-002").</summary>
        Public Function ToplamMiktar(ParamArray urunKodlari As String()) As Integer
            SyncLock _kilit
                Return _kalemler.Values.
                    Where(Function(k) urunKodlari.Any(Function(u) String.Equals(u, k.UrunKodu, StringComparison.OrdinalIgnoreCase))).
                    Sum(Function(k) k.Miktar)
            End SyncLock
        End Function

        ''' <summary>Depolar arası transfer (atomik: ya hep ya hiç).</summary>
        Public Function Transfer(urunKodu As String, adet As Integer, kaynakDepo As String, hedefDepo As String) As Boolean
            SyncLock _kilit
                If Miktar(urunKodu, kaynakDepo) < adet Then Return False
                Cikis(urunKodu, adet, kaynakDepo)
                Giris(urunKodu, adet, hedefDepo)
                Return True
            End SyncLock
        End Function

        Public Function Liste(Optional depo As String = Nothing) As List(Of StokKalemi)
            SyncLock _kilit
                Return _kalemler.Values.
                    Where(Function(k) depo Is Nothing OrElse String.Equals(k.Depo, depo, StringComparison.OrdinalIgnoreCase)).
                    OrderBy(Function(k) k.Depo).ThenBy(Function(k) k.UrunKodu).
                    Select(Function(k) k.Kopya()).
                    ToList()
            End SyncLock
        End Function

        ''' <summary>
        ''' Sayım: her depo ayrı bir görevde (sayım cihazı taklidi, depo başına ~100 ms) PARALEL sayılır.
        ''' Depo verilmezse tüm depolar.
        ''' </summary>
        Public Async Function SayimAsync(Optional depo As String = Nothing) As Task(Of SayimSonucu)
            Dim basla = DateTime.Now
            Dim depolar As List(Of String)
            SyncLock _kilit
                depolar = _kalemler.Values.Select(Function(k) k.Depo).Distinct(StringComparer.OrdinalIgnoreCase).
                    Where(Function(x) depo Is Nothing OrElse String.Equals(x, depo, StringComparison.OrdinalIgnoreCase)).ToList()
            End SyncLock
            If depolar.Count = 0 Then Throw New ArgumentException($"'{depo}' deposu yok.")

            Dim gorevler = depolar.Select(Function(d) DepoSayAsync(d)).ToList()
            Dim sonuclar = Await Task.WhenAll(gorevler).ConfigureAwait(False)
            Return New SayimSonucu With {
                .Depolar = sonuclar.ToList(),
                .ToplamMiktar = sonuclar.Sum(Function(s) s.ToplamMiktar),
                .KritikKalemler = sonuclar.SelectMany(Function(s) s.KritikKalemler).ToList(),
                .SureMs = CLng((DateTime.Now - basla).TotalMilliseconds)
            }
        End Function

        Private Async Function DepoSayAsync(depo As String) As Task(Of DepoSayimi)
            Await Task.Delay(100).ConfigureAwait(False)
            SyncLock _kilit
                Dim kalemler = _kalemler.Values.Where(Function(k) String.Equals(k.Depo, depo, StringComparison.OrdinalIgnoreCase)).ToList()
                Return New DepoSayimi With {
                    .Depo = depo,
                    .KalemSayisi = kalemler.Count,
                    .ToplamMiktar = kalemler.Sum(Function(k) k.Miktar),
                    .KritikKalemler = kalemler.Where(Function(k) k.Miktar < KritikSeviye).Select(Function(k) $"{k.Depo}/{k.UrunKodu} ({k.Miktar})").ToList()
                }
            End SyncLock
        End Function

        Private Shared Function Anahtar(urunKodu As String, depo As String) As String
            Return depo.ToUpperInvariant() & "|" & urunKodu.ToUpperInvariant()
        End Function

    End Class

    Public Class StokKalemi
        Public Property UrunKodu As String = ""
        Public Property Depo As String = ""
        Public Property Miktar As Integer
        Public Property SonHareket As DateTime

        Friend Function Kopya() As StokKalemi
            Return DirectCast(MemberwiseClone(), StokKalemi)
        End Function
    End Class

    Public Class DepoSayimi
        Public Property Depo As String = ""
        Public Property KalemSayisi As Integer
        Public Property ToplamMiktar As Integer
        Public Property KritikKalemler As New List(Of String)
    End Class

    Public Class SayimSonucu
        Public Property Depolar As New List(Of DepoSayimi)
        Public Property ToplamMiktar As Integer
        Public Property KritikKalemler As New List(Of String)
        Public Property SureMs As Long
    End Class

    Public Class KritikStokEventArgs
        Inherits EventArgs
        Public Property UrunKodu As String = ""
        Public Property Depo As String = ""
        Public Property Kalan As Integer
        Public Property Kritik As Integer
    End Class

End Namespace
