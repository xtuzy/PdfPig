﻿namespace UglyToad.PdfPig.Writer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Content;
    using Fonts;
    using Fonts.TrueType;
    using Fonts.TrueType.Parser;
    using Geometry;
    using Graphics.Operations;
    using IO;
    using Tokens;
    using Util;
    using Util.JetBrains.Annotations;

    internal class PdfDocumentBuilder
    {
        private const byte Break = (byte) '\n';
        private static readonly TrueTypeFontParser Parser = new TrueTypeFontParser();

        private readonly Dictionary<int, PdfPageBuilder> pages = new Dictionary<int, PdfPageBuilder>();
        private readonly Dictionary<Guid, FontStored> fonts = new Dictionary<Guid, FontStored>();

        public IReadOnlyDictionary<int, PdfPageBuilder> Pages => pages;
        public IReadOnlyDictionary<Guid, IWritingFont> Fonts => fonts.ToDictionary(x => x.Key, x => x.Value.FontProgram);

        public bool CanUseTrueTypeFont(IReadOnlyList<byte> fontFileBytes, out IReadOnlyList<string> reasons)
        {
            var reasonsMutable = new List<string>();
            reasons = reasonsMutable;
            try
            {
                if (fontFileBytes == null)
                {
                    reasonsMutable.Add("Provided bytes were null.");
                    return false;
                }

                if (fontFileBytes.Count == 0)
                {
                    reasonsMutable.Add("Provided bytes were empty.");
                    return false;
                }

                var font = Parser.Parse(new TrueTypeDataBytes(new ByteArrayInputBytes(fontFileBytes)));

                if (font.TableRegister.CMapTable == null)
                {
                    reasonsMutable.Add("The provided font did not contain a cmap table, used to map character codes to glyph codes.");
                    return false;
                }

                if (font.TableRegister.Os2Table == null)
                {
                    reasonsMutable.Add("The provided font did not contain an OS/2 table, used to fill in the font descriptor dictionary.");
                    return false;
                }

                if (font.TableRegister.PostScriptTable == null)
                {
                    reasonsMutable.Add("The provided font did not contain a post PostScript table, used to map character codes to glyph codes.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reasonsMutable.Add(ex.Message);
                return false;
            }
        }

        public AddedFont AddTrueTypeFont(IReadOnlyList<byte> fontFileBytes)
        {
            try
            {
                var font = Parser.Parse(new TrueTypeDataBytes(new ByteArrayInputBytes(fontFileBytes)));
                var id = Guid.NewGuid();
                var i = fonts.Count;
                var added = new AddedFont(id, NameToken.Create($"F{i}"));
                fonts[id] = new FontStored(added, new TrueTypeWritingFont(font, fontFileBytes));

                return added;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Writing only supports TrueType fonts, please provide a valid TrueType font.", ex);
            }
        }

        public AddedFont AddStandard14Font(Standard14Font type)
        {
            var id = Guid.NewGuid();
            var name = NameToken.Create($"F{fonts.Count}");
            var added = new AddedFont(id, name);
            fonts[id] = new FontStored(added, new Standard14WritingFont(Standard14.GetAdobeFontMetrics(type)));

            return added;
        }

        public PdfPageBuilder AddPage(PageSize size, bool isPortrait = true)
        {
            if (!size.TryGetPdfRectangle(out var rectangle))
            {
                throw new ArgumentException($"No rectangle found for Page Size {size}.");
            }

            if (!isPortrait)
            {
                rectangle = new PdfRectangle(0, 0, rectangle.Height, rectangle.Width);
            }

            PdfPageBuilder builder = null;
            for (var i = 0; i < pages.Count; i++)
            {
                if (!pages.ContainsKey(i + 1))
                {
                    builder = new PdfPageBuilder(i + 1, this);
                    break;
                }
            }

            if (builder == null)
            {
                builder = new PdfPageBuilder(pages.Count + 1, this);
            }

            builder.PageSize = rectangle;
            pages[builder.PageNumber] = builder;

            return builder;
        }

        public void Generate(Stream stream)
        {

        }

        public void Generate(string fileName)
        {

        }

        public byte[] Build()
        {
            var context = new BuilderContext();
            var fontsWritten = new Dictionary<Guid, ObjectToken>();
            using (var memory = new MemoryStream())
            {
                // Header
                WriteString("%PDF-1.7", memory);

                // Body
                foreach (var font in fonts)
                {
                    var fontObj = font.Value.FontProgram.WriteFont(font.Value.FontKey.Name, memory, context);
                    fontsWritten.Add(font.Key, fontObj);
                }

                var resources = new Dictionary<NameToken, IToken>
                {
                    { NameToken.ProcSet, new ArrayToken(new []{ NameToken.Create("PDF"), NameToken.Create("Text") }) }
                };

                if (fontsWritten.Count > 0)
                {
                    var fontsDictionary = new DictionaryToken(fontsWritten.Select(x => (fonts[x.Key].FontKey.Name, (IToken)new IndirectReferenceToken(x.Value.Number)))
                        .ToDictionary(x => x.Item1, x => x.Item2));

                    var fontsDictionaryRef = context.WriteObject(memory, fontsDictionary);

                    resources.Add(NameToken.Font, new IndirectReferenceToken(fontsDictionaryRef.Number));
                }

                var pageReferences = new List<IndirectReferenceToken>();
                foreach (var page in pages)
                {
                    var pageDictionary = new Dictionary<NameToken, IToken>
                    {
                        {NameToken.Type, NameToken.Page},
                        {
                            NameToken.Resources,
                            new DictionaryToken(resources)
                        },
                        {NameToken.MediaBox, RectangleToArray(page.Value.PageSize)}
                    };

                    if (page.Value.Operations.Count > 0)
                    {
                        var contentStream = WriteContentStream(page.Value.Operations);

                        var contentStreamObj = context.WriteObject(memory, contentStream);

                        pageDictionary[NameToken.Contents] = new IndirectReferenceToken(contentStreamObj.Number);
                    }

                    var pageRef = context.WriteObject(memory, new DictionaryToken(pageDictionary));

                    pageReferences.Add(new IndirectReferenceToken(pageRef.Number));
                }

                var pagesDictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
                {
                    { NameToken.Type, NameToken.Pages },
                    { NameToken.Kids, new ArrayToken(pageReferences) },
                    { NameToken.Count, new NumericToken(1) }
                });

                var pagesRef = context.WriteObject(memory, pagesDictionary);

                var catalog = new DictionaryToken(new Dictionary<NameToken, IToken>
                {
                    { NameToken.Type, NameToken.Catalog },
                    { NameToken.Pages, new IndirectReferenceToken(pagesRef.Number) }
                });

                var catalogRef = context.WriteObject(memory, catalog);

                TokenWriter.WriteCrossReferenceTable(context.ObjectOffsets, catalogRef, memory);

                return memory.ToArray();
            }
        }

        private static StreamToken WriteContentStream(IReadOnlyList<IGraphicsStateOperation> content)
        {
            using (var memoryStream = new MemoryStream())
            {
                foreach (var operation in content)
                {
                    operation.Write(memoryStream);
                }

                var bytes = memoryStream.ToArray();

                var streamDictionary = new Dictionary<NameToken, IToken>
                {
                    { NameToken.Length, new NumericToken(bytes.Length) }
                };

                var stream = new StreamToken(new DictionaryToken(streamDictionary), bytes);

                return stream;
            }
        }

        private static ArrayToken RectangleToArray(PdfRectangle rectangle)
        {
            return new ArrayToken(new[]
            {
                new NumericToken(rectangle.BottomLeft.X),
                new NumericToken(rectangle.BottomLeft.Y),
                new NumericToken(rectangle.TopRight.X),
                new NumericToken(rectangle.TopRight.Y)
            });
        }
        
        private static void WriteString(string text, MemoryStream stream, bool appendBreak = true)
        {
            var bytes = OtherEncodings.StringAsLatin1Bytes(text);
            stream.Write(bytes, 0, bytes.Length);
            if (appendBreak)
            {
                stream.WriteByte(Break);
            }
        }

        internal class FontStored
        {
            [NotNull]
            public AddedFont FontKey { get; }

            [NotNull]
            public IWritingFont FontProgram { get; }

            public FontStored(AddedFont fontKey, IWritingFont fontProgram)
            {
                FontKey = fontKey ?? throw new ArgumentNullException(nameof(fontKey));
                FontProgram = fontProgram ?? throw new ArgumentNullException(nameof(fontProgram));
            }
        }

        public class AddedFont
        {
            public Guid Id { get; }

            public NameToken Name { get; }

            internal AddedFont(Guid id, NameToken name)
            {
                Id = id;
                Name = name ?? throw new ArgumentNullException(nameof(name));
            }
        }
    }
}