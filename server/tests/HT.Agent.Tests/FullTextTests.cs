using System.Text.Json;
using HT.Agent.Infrastructure.Clients;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace HT.Agent.Tests;

/// <summary>论文的开放全文：取全文地址、读 PDF 文字。</summary>
public class FullTextTests
{
    static JsonElement W(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Full_text_prefers_pdf_then_landing_page_then_oa_url()
    {
        Assert.Equal("https://x.org/a.pdf", LiteratureClient.FullTextUrl(W("""{"best_oa_location":{"pdf_url":"https://x.org/a.pdf","landing_page_url":"https://x.org/a"}}""")));
        Assert.Equal("https://x.org/a", LiteratureClient.FullTextUrl(W("""{"best_oa_location":{"pdf_url":null,"landing_page_url":"https://x.org/a"}}""")));
        Assert.Equal("https://y.org/b", LiteratureClient.FullTextUrl(W("""{"best_oa_location":null,"open_access":{"is_oa":true,"oa_url":"https://y.org/b"}}""")));
        Assert.Null(LiteratureClient.FullTextUrl(W("""{"best_oa_location":null,"open_access":{"is_oa":false,"oa_url":null}}""")));
    }

    [Fact]
    public void Pdf_text_is_extracted_and_scans_are_reported()
    {
        var b = new PdfDocumentBuilder();
        var page = b.AddPage(PageSize.A4);
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var lines = new[]
        {
            "Selective hydrogenation of phenol to cyclohexanone over Pd/C.",
            "Experimental: the reaction was carried out in a 100 mL stainless steel autoclave.",
            "Conditions: 100 C, hydrogen pressure 1.0 MPa, water as solvent, 2 h, stirring at 800 rpm.",
            "Results: conversion 99 percent and selectivity to cyclohexanone 95 percent.",
        };
        for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 10, new PdfPoint(30, 780 - 16 * i), font);
        var (text, note) = WebFetcher.PdfText.Extract(b.Build());
        Assert.Null(note);
        Assert.Contains("1.0 MPa", text);
        Assert.Contains("autoclave", text);

        var empty = new PdfDocumentBuilder();
        empty.AddPage(PageSize.A4);
        var (t2, n2) = WebFetcher.PdfText.Extract(empty.Build());
        Assert.Equal("", t2);
        Assert.Contains("扫描件", n2);

        Assert.Contains("打不开", WebFetcher.PdfText.Extract([1, 2, 3]).Note);
    }
}
