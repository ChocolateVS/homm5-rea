// For each MSVC RTTI type name (e.g. ".?AVCParticlesLoader@NGScene@@"), find the vtable(s) via
// TypeDescriptor -> CompleteObjectLocator -> vtable, and list the virtual function entries.
// Usage: -postScript Vtables.java <namesFile> <outFile>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public class Vtables extends GhidraScript {
    Memory mem;
    List<Address> findDword(int value) throws Exception {
        List<Address> res = new ArrayList<>();
        byte[] pat = new byte[] { (byte) value, (byte) (value >> 8), (byte) (value >> 16), (byte) (value >> 24) };
        Address a = mem.getMinAddress();
        while (true) {
            a = mem.findBytes(a, pat, null, true, monitor);
            if (a == null) break;
            res.add(a);
            a = a.add(1);
        }
        return res;
    }
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        mem = currentProgram.getMemory();
        PrintWriter out = new PrintWriter(new FileWriter(args[1]));
        Listing listing = currentProgram.getListing();
        for (String name : Files.readAllLines(Paths.get(args[0]))) {
            name = name.trim();
            if (name.isEmpty()) continue;
            Address s = mem.findBytes(mem.getMinAddress(), (name + "\0").getBytes("US-ASCII"), null, true, monitor);
            if (s == null) { out.println("## " + name + " not found"); continue; }
            Address td = s.subtract(8);
            out.println("## " + name + " TD=" + td);
            for (Address ref : findDword((int) td.getOffset())) {
                // COL: sig(0) off(4) cdOff(8) pTD(12) pCHD(16); ref is at COL+12
                Address col = ref.subtract(12);
                int sig = mem.getInt(col);
                if (sig != 0) continue;
                int off = mem.getInt(col.add(4));
                for (Address vref : findDword((int) col.getOffset())) {
                    Address vt = vref.add(4);
                    out.println("  vtable " + vt + " (this offset " + off + ")");
                    for (int i = 0; i < 40; i++) {
                        int fp = mem.getInt(vt.add(i * 4));
                        Address fa = toAddr(fp & 0xffffffffL);
                        MemoryBlock b = mem.getBlock(fa);
                        if (b == null || !b.isExecute()) break;
                        Function f = listing.getFunctionAt(fa);
                        out.println("    [" + i + "] " + fa + (f != null ? " " + f.getName() : ""));
                    }
                }
            }
        }
        out.close();
    }
}
