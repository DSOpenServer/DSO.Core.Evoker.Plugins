Imports System
Imports System.Collections.Generic
Imports System.Linq

Namespace Stok

    ''' <summary>
    ''' Örnek plugin (VB.NET) - Stok servisi, SÜRÜM 1.
    ''' VB'ye özgü noktalar: Optional parametreler (varsayılan değerli), büyük/küçük harf duyarsız adlar
    ''' (API'de "giris" / "GIRIS" / "Giris" aynı), ReadOnly property'ler, Sub (dönüşsüz) ve Function.
    ''' </summary>
    Public Class StokServisi

        Private ReadOnly _kalemler As New Dictionary(Of String, StokKalemi)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _kilit As New Object()

        Public Sub New(Optional varsayilanDepo As String = "ANA")
            Me.VarsayilanDepo = varsayilanDepo
            ' Demo verisi
            Giris("KLM-001", 120)
            Giris("KLM-002", 40)
            Giris("DFT-010", 15, "SUBE1")
        End Sub

        Public ReadOnly Property Surum As String
            Get
                Return "1.0"
            End Get
        End Property

        Public ReadOnly Property VarsayilanDepo As String

        Public ReadOnly Property KalemSayisi As Integer
            Get
                SyncLock _kilit
                    Return _kalemler.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>Stok girişi. Depo verilmezse VarsayilanDepo.</summary>
        Public Sub Giris(urunKodu As String, adet As Integer, Optional depo As String = Nothing)
            If String.IsNullOrWhiteSpace(urunKodu) Then Throw New ArgumentException("Ürün kodu boş olamaz.", NameOf(urunKodu))
            If adet <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(adet), "Adet pozitif olmalı.")
            Dim d = If(depo, VarsayilanDepo)
            SyncLock _kilit
                Dim k As StokKalemi = Nothing
                If Not _kalemler.TryGetValue(Anahtar(urunKodu, d), k) Then
                    k = New StokKalemi With {.UrunKodu = urunKodu.ToUpperInvariant(), .Depo = d}
                    _kalemler(Anahtar(urunKodu, d)) = k
                End If
                k.Miktar += adet
                k.SonHareket = DateTime.Now
            End SyncLock
        End Sub

        ''' <summary>Stok çıkışı. Yetersizse False döner (exception değil).</summary>
        Public Function Cikis(urunKodu As String, adet As Integer, Optional depo As String = Nothing) As Boolean
            If adet <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(adet), "Adet pozitif olmalı.")
            SyncLock _kilit
                Dim k As StokKalemi = Nothing
                If Not _kalemler.TryGetValue(Anahtar(urunKodu, If(depo, VarsayilanDepo)), k) OrElse k.Miktar < adet Then Return False
                k.Miktar -= adet
                k.SonHareket = DateTime.Now
                Return True
            End SyncLock
        End Function

        Public Function Miktar(urunKodu As String, Optional depo As String = Nothing) As Integer
            SyncLock _kilit
                Dim k As StokKalemi = Nothing
                Return If(_kalemler.TryGetValue(Anahtar(urunKodu, If(depo, VarsayilanDepo)), k), k.Miktar, 0)
            End SyncLock
        End Function

        ''' <summary>Tüm kalemler (depo verilirse sadece o depo).</summary>
        Public Function Liste(Optional depo As String = Nothing) As List(Of StokKalemi)
            SyncLock _kilit
                Return _kalemler.Values.
                    Where(Function(k) depo Is Nothing OrElse String.Equals(k.Depo, depo, StringComparison.OrdinalIgnoreCase)).
                    OrderBy(Function(k) k.Depo).ThenBy(Function(k) k.UrunKodu).
                    Select(Function(k) k.Kopya()).
                    ToList()
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

End Namespace
