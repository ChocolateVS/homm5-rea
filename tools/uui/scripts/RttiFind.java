// Lists RTTI type descriptors whose name contains a substring, with the vtables using them.
// Usage: RttiFind.java <out> <substr>...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;

public class RttiFind extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Memory mem = currentProgram.getMemory();
        byte[] pat = ".?AV".getBytes();
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isInitialized()) continue;
            Address p = b.getStart();
            while (p != null && p.compareTo(b.getEnd()) < 0) {
                Address f = mem.findBytes(p, b.getEnd(), pat, null, true, monitor);
                if (f == null) break;
                StringBuilder sb = new StringBuilder();
                for (int k = 0; k < 120; k++) { byte c = mem.getByte(f.add(k)); if (c == 0) break; sb.append((char) c); }
                String s = sb.toString();
                boolean hit = false;
                for (int i = 1; i < a.length; i++) if (s.contains(a[i])) hit = true;
                if (hit) {
                    Address td = f.subtract(8);
                    out.println(td + " " + s);
                    // COLs referencing td (+12), then vtables referencing COL (vt-4)
                    for (Reference r : getReferencesTo(td)) {
                        Address col = r.getFromAddress().subtract(12);
                        for (Reference r2 : getReferencesTo(col)) out.println("    vt " + r2.getFromAddress().add(4));
                    }
                }
                p = f.add(4);
            }
        }
        out.close();
    }
}
