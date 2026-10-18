// Dumps luaL_reg-style tables {const char* name; lua_CFunction f;} around given addresses.
// Walks backwards and forwards from each seed while entries look like (string ptr, code ptr).
// Usage: -postScript RegTables.java <outFile> <seedAddr> [seedAddr...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;

public class RegTables extends GhidraScript {
    Memory mem;

    String cstr(long p) {
        try {
            Address a = toAddr(p);
            MemoryBlock b = mem.getBlock(a);
            if (b == null || b.isExecute()) return null;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 80; i++) {
                byte c = mem.getByte(a.add(i));
                if (c == 0) return sb.length() > 0 ? sb.toString() : null;
                if (c < 0x20 || c >= 0x7f) return null;
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

    boolean entry(Address a) throws Exception {
        long n = mem.getInt(a) & 0xffffffffL, f = mem.getInt(a.add(4)) & 0xffffffffL;
        return cstr(n) != null && isCode(f);
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        mem = currentProgram.getMemory();
        PrintWriter out = new PrintWriter(new FileWriter(args[0]));
        Set<Long> seen = new HashSet<>();
        for (int i = 1; i < args.length; i++) {
            Address seed = toAddr(args[i]);
            // align: the seed is the name field of an entry
            Address s = seed;
            while (entry(s.subtract(8))) s = s.subtract(8);
            if (seen.contains(s.getOffset())) continue;
            seen.add(s.getOffset());
            out.println("TABLE " + s);
            Address a = s;
            while (entry(a)) {
                long n = mem.getInt(a) & 0xffffffffL, f = mem.getInt(a.add(4)) & 0xffffffffL;
                Function fn = currentProgram.getListing().getFunctionAt(toAddr(f));
                out.println(String.format("  %s 0x%08x %s", cstr(n), f, fn == null ? "" : fn.getName()));
                a = a.add(8);
            }
        }
        out.close();
    }
}
