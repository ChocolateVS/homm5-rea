// Finds vtables for RTTI type descriptors by scanning memory for COL (td at +12) and the COL pointer (vt-4).
// Usage: VtOfTd.java <out> <td>...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import java.io.*;

public class VtOfTd extends GhidraScript {
    byte[] le(long v) { return new byte[]{(byte)v,(byte)(v>>8),(byte)(v>>16),(byte)(v>>24)}; }
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Memory mem = currentProgram.getMemory();
        for (int i = 1; i < a.length; i++) {
            long td = Long.parseLong(a[i], 16);
            out.println("TD " + a[i]);
            for (MemoryBlock b : mem.getBlocks()) {
                if (!b.isInitialized()) continue;
                Address p = b.getStart();
                while (true) {
                    Address f = mem.findBytes(p, b.getEnd(), le(td), null, true, monitor);
                    if (f == null) break;
                    Address col = f.subtract(12);
                    if (mem.getInt(col) == 0) {
                        for (MemoryBlock b2 : mem.getBlocks()) {
                            if (!b2.isInitialized()) continue;
                            Address q = b2.getStart();
                            while (true) {
                                Address g = mem.findBytes(q, b2.getEnd(), le(col.getOffset()), null, true, monitor);
                                if (g == null) break;
                                out.println("  COL " + col + " off=" + mem.getInt(col.add(4)) + " vt " + g.add(4));
                                q = g.add(4);
                            }
                        }
                    }
                    p = f.add(4);
                }
            }
        }
        out.close();
    }
}
