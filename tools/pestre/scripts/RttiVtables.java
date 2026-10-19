// Prints the vtable(s) of MSVC classes whose RTTI type name contains each given substring.
// Usage: -postScript RttiVtables.java <outFile> <nameSubstring> [nameSubstring...]
// Walks TypeDescriptor (".?AV<name>@@") -> CompleteObjectLocator (refs at TD-0xC slot) ->
// vtable (the dword after a pointer to the COL), then lists vtable slots until a non-code pointer.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class RttiVtables extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(args[0]));
        Memory mem = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();
        ReferenceManager rm = currentProgram.getReferenceManager();
        AddressSpace space = currentProgram.getAddressFactory().getDefaultAddressSpace();
        for (int i = 1; i < args.length; i++) {
            String want = args[i];
            byte[] pat = (".?AV" + want + "@@").getBytes("US-ASCII");
            Address a = mem.findBytes(mem.getMinAddress(), pat, null, true, monitor);
            if (a == null) { pat = (".?AU" + want + "@@").getBytes("US-ASCII"); a = mem.findBytes(mem.getMinAddress(), pat, null, true, monitor); }
            if (a == null) { out.println("NOT FOUND " + want); continue; }
            Address td = a.subtract(8); // TypeDescriptor: vfptr, spare, name
            out.println("== " + want + " TypeDescriptor @ " + td);
            // COLs reference the TD at offset +12. Search memory for the TD address value.
            byte[] tdBytes = le(td.getOffset());
            Address s = mem.getMinAddress();
            while (true) {
                s = mem.findBytes(s, tdBytes, null, true, monitor);
                if (s == null) break;
                Address col = s.subtract(12);
                int sig = mem.getInt(col);
                int off = mem.getInt(col.add(4));
                if (sig == 0) {
                    // vtable[-1] holds a pointer to the COL.
                    byte[] colBytes = le(col.getOffset());
                    Address v = mem.getMinAddress();
                    while (true) {
                        v = mem.findBytes(v, colBytes, null, true, monitor);
                        if (v == null) break;
                        Address vt = v.add(4);
                        out.println("  COL @ " + col + " (this-offset " + off + ") vtable @ " + vt);
                        for (int k = 0; k < 64; k++) {
                            long p = mem.getInt(vt.add(4L * k)) & 0xffffffffL;
                            Address fa = space.getAddress(p);
                            Function f = listing.getFunctionAt(fa);
                            if (f == null && (listing.getInstructionAt(fa) == null)) break;
                            out.println("    [" + k + "] +" + Integer.toHexString(4 * k) + " " + fa + (f != null ? " " + f.getName() : ""));
                        }
                        v = v.add(1);
                    }
                }
                s = s.add(1);
            }
        }
        out.close();
    }

    static byte[] le(long v) {
        return new byte[] { (byte) v, (byte) (v >> 8), (byte) (v >> 16), (byte) (v >> 24) };
    }
}
