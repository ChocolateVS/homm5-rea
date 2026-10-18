// Finds static initialisers "push <shipPath>; mov ecx, <global>" and lists code referencing each global.
// Usage: -postScript ShipGlobals.java <outFile> <hexPath>...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;
public class ShipGlobals extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Memory m = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();
        Map<Long, String> paths = new HashMap<>();
        for (int i = 1; i < a.length; i++) paths.put(Long.parseLong(a[i], 16), a[i]);
        MemoryBlock text = m.getBlock(".text");
        Address start = text.getStart(), end = text.getEnd();
        byte[] buf = new byte[(int) text.getSize()];
        m.getBytes(start, buf);
        Map<Long, String> globals = new TreeMap<>();
        for (int i = 0; i + 10 < buf.length; i++) {
            if ((buf[i] & 0xff) != 0x68 || (buf[i + 5] & 0xff) != 0xb9) continue;
            long imm = (buf[i + 1] & 0xffL) | (buf[i + 2] & 0xffL) << 8 | (buf[i + 3] & 0xffL) << 16 | (buf[i + 4] & 0xffL) << 24;
            if (!paths.containsKey(imm)) continue;
            long g = (buf[i + 6] & 0xffL) | (buf[i + 7] & 0xffL) << 8 | (buf[i + 8] & 0xffL) << 16 | (buf[i + 9] & 0xffL) << 24;
            globals.put(g, paths.get(imm));
        }
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (var e : globals.entrySet()) {
            Address g = toAddr(e.getKey());
            List<String> users = new ArrayList<>();
            for (Reference r : rm.getReferencesTo(g)) {
                Function f = listing.getFunctionContaining(r.getFromAddress());
                if (f != null) users.add(f.getName() + "@" + r.getFromAddress());
            }
            if (!users.isEmpty()) out.println(String.format("%08x %s -> %s", e.getKey(), e.getValue(), users));
        }
        out.println("globals: " + globals.size());
        out.close();
    }
}
