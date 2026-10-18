// Scans all initialized non-executable memory for runs of {char* name; code* fn} pairs
// (Lua registration tables). Prints every run of at least <minLen> entries.
// Usage: -postScript ScanRegTables.java <outFile> <minLen>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.listing.*;
import java.io.*;

public class ScanRegTables extends GhidraScript {
    Memory mem;

    String cstr(long p) {
        try {
            Address a = toAddr(p);
            MemoryBlock b = mem.getBlock(a);
            if (b == null || b.isExecute() || !b.isInitialized()) return null;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 64; i++) {
                byte c = mem.getByte(a.add(i));
                if (c == 0) return sb.length() > 1 ? sb.toString() : null;
                if (!(Character.isLetterOrDigit(c) || c == '_')) return null;
                sb.append((char) c);
            }
        } catch (Exception e) { }
        return null;
    }

    boolean isCode(long p) {
        try {
            MemoryBlock b = mem.getBlock(toAddr(p));
            return b != null && b.isExecute();
        } catch (Exception e) { return false; }
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        mem = currentProgram.getMemory();
        int minLen = Integer.parseInt(args[1]);
        PrintWriter out = new PrintWriter(new FileWriter(args[0]));
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isInitialized() || b.isExecute()) continue;
            long start = b.getStart().getOffset(), end = b.getEnd().getOffset();
            for (int phase = 0; phase < 8; phase++) {
                long runStart = -1; int runLen = 0;
                for (long p = start + phase; p + 8 <= end; p += 8) {
                    Address a = toAddr(p);
                    long n = mem.getInt(a) & 0xffffffffL, f = mem.getInt(a.add(4)) & 0xffffffffL;
                    boolean ok = cstr(n) != null && isCode(f);
                    if (ok) { if (runStart < 0) runStart = p; runLen++; }
                    if (!ok || p + 16 > end) {
                        if (runLen >= minLen) {
                            out.println(String.format("TABLE %08x len=%d", runStart, runLen));
                            for (long q = runStart; q < runStart + runLen * 8L; q += 8) {
                                Address qa = toAddr(q);
                                long nn = mem.getInt(qa) & 0xffffffffL, ff = mem.getInt(qa.add(4)) & 0xffffffffL;
                                out.println(String.format("  %s 0x%08x", cstr(nn), ff));
                            }
                        }
                        runStart = -1; runLen = 0;
                    }
                }
            }
        }
        out.close();
    }
}
