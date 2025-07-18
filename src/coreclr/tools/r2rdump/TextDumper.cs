// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using ILCompiler.Reflection.ReadyToRun;
using Internal.ReadyToRunConstants;
using Internal.Runtime;

namespace R2RDump
{
    internal sealed class TextDumper : Dumper
    {
        public TextDumper(ReadyToRunReader r2r, TextWriter writer, Disassembler disassembler, DumpModel model)
            : base(r2r, writer, disassembler, model)
        {
        }

        public override void Begin()
        {
            if (!_model.Normalize)
            {
                _writer.WriteLine($"Filename: {_r2r.Filename}");
                _writer.WriteLine($"OS: {_r2r.OperatingSystem}");
                _writer.WriteLine($"Machine: {_r2r.Machine}");
                _writer.WriteLine($"ImageBase: 0x{_r2r.ImageBase:X8}");
                SkipLine();
            }
        }

        public override void End()
        {
            _writer.WriteLine("=============================================================");
            SkipLine();
        }

        public override void WriteDivider(string title)
        {
            int len = Math.Max(61 - title.Length - 2, 2);
            _writer.WriteLine(new String('=', len / 2) + " " + title + " " + new String('=', (len + 1) / 2));
            SkipLine();
        }

        public override void WriteSubDivider()
        {
            _writer.WriteLine("_______________________________________________");
            SkipLine();
        }

        public override void SkipLine()
        {
            _writer.WriteLine();
        }

        /// <summary>
        /// Dumps the R2RHeader and all the sections in the header
        /// </summary>
        public override void DumpHeader(bool dumpSections)
        {
            _writer.WriteLine(_r2r.ReadyToRunHeader.ToString());

            if (_model.Raw)
            {
                DumpBytes(_r2r.ReadyToRunHeader.RelativeVirtualAddress, (uint)_r2r.ReadyToRunHeader.Size);
            }
            SkipLine();
            if (dumpSections)
            {
                WriteDivider("R2R Sections");
                _writer.WriteLine($"{_r2r.ReadyToRunHeader.Sections.Count} sections");
                SkipLine();

                foreach (ReadyToRunSection section in NormalizedSections(_r2r.ReadyToRunHeader))
                {
                    DumpSection(section);
                }

                if (_r2r.Composite)
                {
                    WriteDivider("Component Assembly Sections");
                    int assemblyIndex = 0;
                    foreach (string assemblyName in _r2r.ManifestReferenceAssemblies.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key))
                    {
                        Guid mvid = _r2r.GetAssemblyMvid(assemblyIndex);
                        WriteDivider($@"Component Assembly [{assemblyIndex}]: {assemblyName} - MVID {mvid:b}");
                        ReadyToRunCoreHeader assemblyHeader = _r2r.ReadyToRunAssemblyHeaders[assemblyIndex];
                        foreach (ReadyToRunSection section in NormalizedSections(assemblyHeader))
                        {
                            DumpSection(section);
                        }
                        assemblyIndex++;
                    }

                }
            }
            SkipLine();
        }

        /// <summary>
        /// Dumps one R2RSection
        /// </summary>
        public override void DumpSection(ReadyToRunSection section)
        {
            WriteSubDivider();
            section.WriteTo(_writer, _model);

            if (_model.Raw)
            {
                DumpBytes(section.RelativeVirtualAddress, (uint)section.Size);
                SkipLine();
            }
            if (_model.SectionContents)
            {
                DumpSectionContents(section);
                SkipLine();
            }
        }

        public override void DumpEntryPoints()
        {
            WriteDivider($@"R2R Entry Points");
            foreach (ReadyToRunMethod method in NormalizedMethods())
            {
                _writer.WriteLine(method.SignatureString);
            }
        }

        public override void DumpAllMethods()
        {
            WriteDivider("R2R Methods");
            _writer.WriteLine($"{_r2r.Methods.Count()} methods");
            SkipLine();

            HashSet<PgoInfoKey> pgoEntriesNotDumped = new HashSet<PgoInfoKey>();

            if (_model.Pgo)
            {
                if (_r2r != null)
                {
                    foreach (PgoInfo info in _r2r.AllPgoInfos)
                    {
                        pgoEntriesNotDumped.Add(info.Key);
                    }
                }
            }
            foreach (ReadyToRunMethod method in NormalizedMethods())
            {
                TextWriter temp = _writer;
                _writer = new StringWriter();
                DumpMethod(method);
                if (_model.Pgo && method.PgoInfo != null)
                {
                    pgoEntriesNotDumped.Remove(method.PgoInfo.Key);
                }
                temp.Write(_writer.ToString());
                _writer = temp;
            }

            if (pgoEntriesNotDumped.Count > 0)
            {
                WriteDivider("Pgo entries without code");
                _writer.WriteLine($"{pgoEntriesNotDumped.Count()} Pgo blobs");
                IEnumerable<PgoInfoKey> pgoEntriesToDump = pgoEntriesNotDumped;
                if (_model.Normalize)
                {
                    pgoEntriesToDump = pgoEntriesToDump.OrderBy((p) => p.SignatureString);
                }
                foreach (var entry in pgoEntriesToDump)
                {
                    WriteSubDivider();
                    _writer.WriteLine(entry.SignatureString);
                    _writer.WriteLine($"Handle: 0x{MetadataTokens.GetToken(entry.ComponentReader.MetadataReader, entry.MethodHandle):X8}");
                    _writer.WriteLine(_r2r.GetPgoInfoByKey(entry));
                }
            }
        }

        /// <summary>
        /// Dumps one R2RMethod.
        /// </summary>
        public override void DumpMethod(ReadyToRunMethod method)
        {
            WriteSubDivider();
            method.WriteTo(_writer, _model);

            if (_model.GC && method.GcInfo != null)
            {
                BaseGcInfo gcInfo = method.GcInfo;
                _writer.WriteLine("GC info:");
                _writer.Write(gcInfo);

                if (_model.Raw)
                {
                    DumpBytes(method.GcInfoRva, (uint)gcInfo.Size);
                }
            }
            SkipLine();

            if (_model.Pgo && method.PgoInfo != null)
            {
                _writer.WriteLine("PGO info:");
                _writer.Write(method.PgoInfo);

                if (_model.Raw)
                {
                    DumpBytes(method.PgoInfo.Offset, (uint)method.PgoInfo.Size, convertToOffset: false);
                }
                SkipLine();
            }

            foreach (RuntimeFunction runtimeFunction in method.RuntimeFunctions)
            {
                DumpRuntimeFunction(runtimeFunction);
            }
        }

        /// <summary>
        /// Dumps one runtime function.
        /// </summary>
        public override void DumpRuntimeFunction(RuntimeFunction rtf)
        {
            _writer.WriteLine(rtf.Method.SignatureString);
            rtf.WriteTo(_writer, _model);

            if (_model.Disasm)
            {
                DumpDisasm(rtf, _r2r.GetOffset(rtf.StartAddress));
            }

            if (_model.Raw)
            {
                _writer.WriteLine("Raw Bytes:");
                DumpBytes(rtf.StartAddress, (uint)rtf.Size);
            }
            if (_model.Unwind)
            {
                _writer.WriteLine("UnwindInfo:");
                _writer.Write(rtf.UnwindInfo);
                if (_model.Raw)
                {
                    DumpBytes(rtf.UnwindRVA, (uint)rtf.UnwindInfo.Size);
                }
            }
            SkipLine();
        }

        /// <summary>
        /// Dumps disassembly and register liveness
        /// </summary>
        public override void DumpDisasm(RuntimeFunction rtf, int imageOffset)
        {
            string indentString = new string(' ', _disassembler.MnemonicIndentation);
            int codeOffset = rtf.CodeOffset;
            int rtfOffset = 0;

            while (rtfOffset < rtf.Size)
            {
                string instr;
                int instrSize = _disassembler.GetInstruction(rtf, imageOffset, rtfOffset, out instr);

                if (_r2r.Machine == Machine.Amd64 && ((ILCompiler.Reflection.ReadyToRun.Amd64.UnwindInfo)rtf.UnwindInfo).CodeOffsetToUnwindCodeIndex.TryGetValue(codeOffset, out int unwindCodeIndex))
                {
                    ILCompiler.Reflection.ReadyToRun.Amd64.UnwindCode code = ((ILCompiler.Reflection.ReadyToRun.Amd64.UnwindInfo)rtf.UnwindInfo).UnwindCodes[unwindCodeIndex];
                    _writer.Write($"{indentString}{code.UnwindOp} {code.OpInfoStr}");
                    if (code.NextFrameOffset != -1)
                    {
                        _writer.WriteLine($"{indentString}{code.NextFrameOffset}");
                    }
                    _writer.WriteLine();
                }
                BaseGcInfo gcInfo = (_model.HideTransitions ? null : rtf.Method?.GcInfo);
                if (gcInfo != null && gcInfo.Transitions != null && gcInfo.Transitions.TryGetValue(codeOffset, out List<BaseGcTransition> transitionsForOffset))
                {
                    string[] formattedTransitions = new string[transitionsForOffset.Count];
                    for (int transitionIndex = 0; transitionIndex < formattedTransitions.Length; transitionIndex++)
                    {
                        formattedTransitions[transitionIndex] = transitionsForOffset[transitionIndex].ToString();
                    }
                    if (_model.Normalize)
                    {
                        Array.Sort(formattedTransitions);
                    }
                    foreach (string transition in formattedTransitions)
                    {
                        _writer.WriteLine($"{indentString}{transition}");
                    }
                }

                /* According to https://msdn.microsoft.com/en-us/library/ck9asaa9.aspx and src/vm/gcinfodecoder.cpp
                 * UnwindCode and GcTransition CodeOffsets are encoded with a -1 adjustment (that is, it's the offset of the start of the next instruction)
                 */
                _writer.Write(instr);

                rtfOffset += instrSize;
                codeOffset += instrSize;
            }
        }

        /// <summary>
        /// Prints a formatted string containing a block of bytes from the relative virtual address and size
        /// </summary>
        public override void DumpBytes(int rva, uint size, string name = "Raw", bool convertToOffset = true)
        {
            int start = rva;
            if (convertToOffset)
                start = _r2r.GetOffset(rva);
            if (start > _r2r.Image.Length || start + size > _r2r.Image.Length)
            {
                throw new IndexOutOfRangeException();
            }

            _writer.Write("    ");
            if (rva % 16 != 0)
            {
                int floor = rva / 16 * 16;
                _writer.Write($"{floor:X8}:");
                _writer.Write(new String(' ', (rva - floor) * 3));
            }
            for (uint i = 0; i < size; i++)
            {
                if ((rva + i) % 16 == 0)
                {
                    _writer.Write($"{rva + i:X8}:");
                }
                _writer.Write($" {_r2r.Image[start + i]:X2}");
                if ((rva + i) % 16 == 15 && i != size - 1)
                {
                    SkipLine();
                    _writer.Write("    ");
                }
            }
            SkipLine();
        }

        public override void DumpSectionContents(ReadyToRunSection section)
        {
            switch (section.Type)
            {
                case ReadyToRunSectionType.AvailableTypes:
                    if (!_model.Naked)
                    {
                        uint availableTypesSectionOffset = (uint)_r2r.GetOffset(section.RelativeVirtualAddress);
                        NativeParser availableTypesParser = new NativeParser(_r2r.ImageReader, availableTypesSectionOffset);
                        NativeHashtable availableTypes = new NativeHashtable(_r2r.ImageReader, availableTypesParser, (uint)(availableTypesSectionOffset + section.Size));
                        _writer.WriteLine(availableTypes.ToString());
                    }

                    int assemblyIndex1 = _r2r.GetAssemblyIndex(section);
                    if (assemblyIndex1 != -1)
                    {
                        _writer.WriteLine();
                        foreach (string name in _r2r.ReadyToRunAssemblies[assemblyIndex1].AvailableTypes)
                        {
                            _writer.WriteLine(name);
                        }
                    }
                    break;
                case ReadyToRunSectionType.MethodDefEntryPoints:
                    if (!_model.Naked)
                    {
                        NativeArray methodEntryPoints = new NativeArray(_r2r.ImageReader, (uint)_r2r.GetOffset(section.RelativeVirtualAddress));
                        _writer.Write(methodEntryPoints.ToString());
                    }

                    int assemblyIndex2 = _r2r.GetAssemblyIndex(section);
                    if (assemblyIndex2 != -1)
                    {
                        _writer.WriteLine();
                        foreach (ReadyToRunMethod method in _r2r.ReadyToRunAssemblies[assemblyIndex2].Methods)
                        {
                            _writer.WriteLine($@"{MetadataTokens.GetToken(method.MethodHandle):X8}: {method.SignatureString}");
                        }
                    }
                    break;
                case ReadyToRunSectionType.InstanceMethodEntryPoints:
                    if (!_model.Naked)
                    {
                        uint instanceSectionOffset = (uint)_r2r.GetOffset(section.RelativeVirtualAddress);
                        NativeParser instanceParser = new NativeParser(_r2r.ImageReader, instanceSectionOffset);
                        NativeHashtable instMethodEntryPoints = new NativeHashtable(_r2r.ImageReader, instanceParser, (uint)(instanceSectionOffset + section.Size));
                        _writer.Write(instMethodEntryPoints.ToString());
                        _writer.WriteLine();
                    }
                    foreach (InstanceMethod instanceMethod in _r2r.InstanceMethods)
                    {
                        _writer.WriteLine($@"0x{instanceMethod.Bucket:X2} -> {instanceMethod.Method.SignatureString}");
                    }
                    break;
                case ReadyToRunSectionType.RuntimeFunctions:
                    int rtfOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int rtfEndOffset = rtfOffset + section.Size;
                    int rtfIndex = 0;
                    _writer.WriteLine("  Index | StartRVA |  EndRVA  | UnwindRVA");
                    _writer.WriteLine("-----------------------------------------");
                    while (rtfOffset < rtfEndOffset)
                    {
                        int startRva = _r2r.ImageReader.ReadInt32(ref rtfOffset);
                        int endRva = -1;
                        if (_r2r.Machine == Machine.Amd64)
                        {
                            endRva = _r2r.ImageReader.ReadInt32(ref rtfOffset);
                        }
                        int unwindRva = _r2r.ImageReader.ReadInt32(ref rtfOffset);
                        string endRvaText = (endRva != -1 ? endRva.ToString("x8") : "        ");
                        _writer.WriteLine($"{rtfIndex,7} | {startRva:X8} | {endRvaText} | {unwindRva:X8}");
                        rtfIndex++;
                    }
                    break;
                case ReadyToRunSectionType.CompilerIdentifier:
                    _writer.WriteLine(_r2r.CompilerIdentifier);
                    break;
                case ReadyToRunSectionType.ImportSections:
                    if (_model.Naked)
                    {
                        List<ReadyToRunImportSection.ImportSectionEntry> entries = new();
                        foreach (ReadyToRunImportSection importSection in _r2r.ImportSections)
                        {
                            entries.AddRange(importSection.Entries);
                        }
                        entries.Sort((e1, e2) => e1.Signature.ToString(_model.SignatureFormattingOptions).CompareTo(e2.Signature.ToString(_model.SignatureFormattingOptions)));
                        foreach (ReadyToRunImportSection.ImportSectionEntry entry in entries)
                        {
                            entry.WriteTo(_writer, _model);
                            _writer.WriteLine();
                        }
                    }
                    else
                    {
                        foreach (ReadyToRunImportSection importSection in _r2r.ImportSections)
                        {
                            importSection.WriteTo(_writer);
                            if (_model.Raw && importSection.Entries.Count != 0)
                            {
                                if (importSection.SectionRVA != 0)
                                {
                                    _writer.WriteLine("Section Bytes:");
                                    DumpBytes(importSection.SectionRVA, (uint)importSection.SectionSize);
                                }
                                if (importSection.SignatureRVA != 0)
                                {
                                    _writer.WriteLine("Signature Bytes:");
                                    DumpBytes(importSection.SignatureRVA, (uint)importSection.Entries.Count * sizeof(int));
                                }
                                if (importSection.AuxiliaryDataRVA != 0 && importSection.AuxiliaryDataSize != 0)
                                {
                                    _writer.WriteLine("AuxiliaryData Bytes:");
                                    DumpBytes(importSection.AuxiliaryDataRVA, (uint)importSection.AuxiliaryDataSize);
                                }
                            }
                            foreach (ReadyToRunImportSection.ImportSectionEntry entry in importSection.Entries)
                            {
                                entry.WriteTo(_writer, _model);
                                _writer.WriteLine();
                            }
                            _writer.WriteLine();
                        }
                    }
                    break;
                case ReadyToRunSectionType.ManifestMetadata:
                    int assemblyRefCount = 0;
                    if (!_r2r.Composite)
                    {
                        MetadataReader globalReader = _r2r.GetGlobalMetadata().MetadataReader;
                        assemblyRefCount = globalReader.GetTableRowCount(TableIndex.AssemblyRef) + 1;
                        _writer.WriteLine($"MSIL AssemblyRef's ({assemblyRefCount} entries):");
                        for (int assemblyRefIndex = 1; assemblyRefIndex < assemblyRefCount; assemblyRefIndex++)
                        {
                            AssemblyReference assemblyRef = globalReader.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(assemblyRefIndex));
                            string assemblyRefName = globalReader.GetString(assemblyRef.Name);
                            _writer.WriteLine($"[ID 0x{assemblyRefIndex:X2}]: {assemblyRefName}");
                        }
                    }

                    _writer.WriteLine($"Manifest metadata AssemblyRef's ({_r2r.ManifestReferenceAssemblies.Count} entries):");
                    int manifestAsmIndex = 0;
                    foreach (string manifestReferenceAssembly in _r2r.ManifestReferenceAssemblies.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key))
                    {
                        _writer.WriteLine($"[ID 0x{manifestAsmIndex + assemblyRefCount + 1:X2}]: {manifestReferenceAssembly}");
                        manifestAsmIndex++;
                    }
                    break;
                case ReadyToRunSectionType.AttributePresence:
                    int attributesStartOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int attributesEndOffset = attributesStartOffset + section.Size;
                    NativeCuckooFilter attributes = new NativeCuckooFilter(_r2r.ImageReader, attributesStartOffset, attributesEndOffset);
                    _writer.WriteLine("Attribute presence filter");
                    _writer.WriteLine(attributes.ToString());
                    break;
                case ReadyToRunSectionType.InliningInfo:
                    int iiOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int iiEndOffset = iiOffset + section.Size;
                    InliningInfoSection inliningInfoSection = new InliningInfoSection(_r2r, iiOffset, iiEndOffset);
                    _writer.WriteLine(inliningInfoSection.ToString());
                    break;
                case ReadyToRunSectionType.InliningInfo2:
                    int ii2Offset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int ii2EndOffset = ii2Offset + section.Size;
                    InliningInfoSection2 inliningInfoSection2 = new InliningInfoSection2(_r2r, ii2Offset, ii2EndOffset);
                    _writer.WriteLine(inliningInfoSection2.ToString());
                    break;
                case ReadyToRunSectionType.OwnerCompositeExecutable:
                    int oceOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    if (_r2r.Image[oceOffset + section.Size - 1] != 0)
                    {
                        Program.WriteWarning("String is not zero-terminated");
                    }
                    _writer.WriteLine("Composite executable: {0}", _r2r.OwnerCompositeExecutable);
                    break;
                case ReadyToRunSectionType.ManifestAssemblyMvids:
                    int mvidCount = section.Size / ReadyToRunReader.GuidByteSize;
                    for (int mvidIndex = 0; mvidIndex < mvidCount; mvidIndex++)
                    {
                        _writer.WriteLine("MVID[{0}] = {1:b}", mvidIndex, _r2r.GetAssemblyMvid(mvidIndex));
                    }
                    break;
                case ReadyToRunSectionType.HotColdMap:
                    int count = section.Size / 8;
                    int hotColdMapOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    for (int i = 0; i < count; i++)
                    {
                        _writer.Write(_r2r.ImageReader.ReadInt32(ref hotColdMapOffset));
                        _writer.Write(",");
                        _writer.WriteLine(_r2r.ImageReader.ReadInt32(ref hotColdMapOffset));
                    }
                    break;
                case ReadyToRunSectionType.MethodIsGenericMap:
                {
                    int mapOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int mapDone = section.Size + mapOffset;
                    int countMethods = _r2r.ImageReader.ReadInt32(ref mapOffset);
                    int curMethod = 1;
                    while ((curMethod <= countMethods) && mapDone > mapOffset)
                    {
                        byte curByte = _r2r.ImageReader.ReadByte(ref mapOffset);
                        for (int i = 0; i < 8 && (curMethod <= countMethods); i++)
                        {
                            bool isGeneric = (curByte & 0x80) == 0x80;
                            _writer.WriteLine($"{curMethod | 0x06000000:x8} : {isGeneric}");
                            curByte <<= 1;
                            curMethod++;
                        }
                    }
                    if (curMethod != (countMethods + 1))
                    {
                        Program.WriteWarning("MethodIsGenericMap malformed");
                        System.Diagnostics.Debug.Fail("MethodIsGenericMap malformed");
                    }
                    break;
                }
                case ReadyToRunSectionType.EnclosingTypeMap:
                {
                    int mapOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int mapDone = section.Size + mapOffset;
                    uint countTypes = checked((uint)((section.Size / 2 - 1))); // 2 bytes per nested type. This data structure is only used for IL images where there are <= 0xFFFE types.
                    if (countTypes != _r2r.ImageReader.ReadUInt16(ref mapOffset))
                    {
                        Program.WriteWarning("EnclosingTypeMap malformed");
                        System.Diagnostics.Debug.Fail("EnclosingTypeMap malformed");
                    }
                    int curType = 1;
                    while (curType <= (countTypes + 1))
                    {
                        _writer.WriteLine($"{curType | 0x02000000:x8} : {_r2r.ImageReader.ReadUInt16(ref mapOffset) | 0x02000000:x8}");
                        curType++;
                    }
                    break;
                }
                case ReadyToRunSectionType.TypeGenericInfoMap:
                {
                    int mapOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int mapDone = section.Size + mapOffset;
                    int countTypes = _r2r.ImageReader.ReadInt32(ref mapOffset);
                    int curType = 1;
                    while ((curType <= countTypes) && mapDone > mapOffset)
                    {
                        byte curByte = _r2r.ImageReader.ReadByte(ref mapOffset);
                        for (int i = 0; i < 2 && (curType <= countTypes); i++)
                        {
                            var genericInfo = (ReadyToRunTypeGenericInfo)((curByte & 0xF0) >> 4);
                            bool hasConstraints = genericInfo.HasFlag(ReadyToRunTypeGenericInfo.HasConstraints);
                            bool hasVariance = genericInfo.HasFlag(ReadyToRunTypeGenericInfo.HasVariance);
                            var genericCount = (ReadyToRunGenericInfoGenericCount)(genericInfo & ReadyToRunTypeGenericInfo.GenericCountMask);
                            _writer.WriteLine($"{curType | 0x06000000:x8} : GenericArgumentCount: {genericCount} HasVariance {hasVariance} HasConstraints {hasConstraints}");
                            curByte <<= 4;
                            curType++;
                        }
                    }
                    if (curType != (countTypes + 1))
                    {
                        Program.WriteWarning("TypeGenericInfoMap malformed");
                        System.Diagnostics.Debug.Fail("TypeGenericInfoMap malformed");
                    }
                    break;
                }

                default:
                    _writer.WriteLine("Unsupported section type {0}", section.Type);
                    break;
            }
        }

        public override void DumpQueryCount(string q, string title, int count)
        {
            _writer.WriteLine(count + " result(s) for \"" + q + "\"");
            SkipLine();
        }

        public override void DumpFixupStats()
        {
            WriteDivider("Eager fixup counts across all methods");

            // Group all fixups across methods by fixup kind, and sum each category
            var sortedFixupCounts = _r2r.Methods.Where(m => m.Fixups != null)
                .SelectMany(m => m.Fixups)
                .GroupBy(f => f.Signature.FixupKind)
                .Select(group => new
                {
                    FixupKind = group.Key,
                    Count = group.Count()
                }).OrderByDescending(x => x.Count);

            /* Runtime Repo GH Issue #49249:
             *
             * In order to format the fixup counts results table, we need to
             * know beforehand the size of each column. The padding is calculated
             * as follows:
             *
             * Fixup: Length of the longest Fixup Kind name.
             * Count: Since a total is always bigger than its operands, we set
             *        the padding to the total's number of digits.
             *
             * The reason we want them to be at least 5, is because in the case of only
             * getting values shorter than 5 digits (Length of "Fixup" and "Count"),
             * the formatting could be messed up. The likelihood of this happening
             * is apparently 0%, but better safe than sorry. */

            int fixupPadding = 5;
            int sortedFixupCountsTotal = sortedFixupCounts.Sum(x => x.Count);
            int countPadding = Math.Max(sortedFixupCountsTotal.ToString().Length, 5);

            /* We look at all the Fixup Kinds that will be printed. We
             * then store the length of the longest one's name. */

            foreach (var fixupAndCount in sortedFixupCounts)
            {
                int kindLength = fixupAndCount.FixupKind.ToString().Length;

                if (kindLength > fixupPadding)
                    fixupPadding = kindLength;
            }

            _writer.WriteLine(
                $"{"Fixup".PadLeft(fixupPadding)} | {"Count".PadLeft(countPadding)}"
            );
            foreach (var fixupAndCount in sortedFixupCounts)
            {
                _writer.WriteLine(
                    $"{fixupAndCount.FixupKind.ToString().PadLeft(fixupPadding)} | {fixupAndCount.Count.ToString().PadLeft(countPadding)}"
                );
            }

            // The +3 in this divider is to account for the " | " table division.
            _writer.WriteLine(new string('-', fixupPadding + countPadding + 3));
            _writer.WriteLine(
                $"{"Total".PadLeft(fixupPadding)} | {sortedFixupCountsTotal.ToString().PadLeft(countPadding)}"
            );
            SkipLine();
        }
    }
}
