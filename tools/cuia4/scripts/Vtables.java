// For each RTTI type descriptor address: finds the complete object locators and prints the vtable entries.
// Usage: -postScript Vtables.java <outFile> <nEntries> <tdAddr> [...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.listing.*;
import java.io.*;
public class Vtables extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        int n = Integer.parseInt(a[1]);
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (int i = 2; i < a.length; i++) {
            Address td = toAddr(a[i]);
            Symbol s = getSymbolAt(td);
            out.println("== " + td + " " + (s == null ? "" : s.getName(true)));
            // scan memory for the TD pointer value (refs may be missing)
            byte[] pat = new byte[4]; long v = td.getOffset();
            for (int k = 0; k < 4; k++) pat[k] = (byte)(v >> (8*k));
            Address p = currentProgram.getMinAddress();
            while ((p = find(p, pat)) != null) {
                Address col = p.subtract(12);
                if (getInt(col) == 0 && getInt(col.add(4)) == 0) {
                    byte[] cp = new byte[4]; long cv = col.getOffset();
                    for (int k = 0; k < 4; k++) cp[k] = (byte)(cv >> (8*k));
                    Address q = currentProgram.getMinAddress();
                    while ((q = find(q, cp)) != null) {
                        Address vt = q.add(4);
                        out.print("  vtable " + vt + ":");
                        for (int k = 0; k < n; k++) out.print(" " + Integer.toHexString(getInt(vt.add(4*k))));
                        out.println();
                        q = q.add(1);
                    }
                }
                p = p.add(1);
            }
        }
        out.close();
    }
}
