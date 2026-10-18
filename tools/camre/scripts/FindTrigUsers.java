// Lists functions that call both sin and cos (CRT _CIsin/_CIcos or sin/cos thunks), with call counts.
// Usage: -postScript FindTrigUsers.java <outFile>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.address.*;
import java.io.*;
import java.util.*;

public class FindTrigUsers extends GhidraScript {
    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter(new FileWriter(getScriptArgs()[0]));
        SymbolTable st = currentProgram.getSymbolTable();
        Map<String, Set<Function>> users = new HashMap<>();
        for (String name : new String[]{"_CIsin", "_CIcos", "sin", "cos", "sinf", "cosf", "_CIatan2", "atan2"}) {
            Set<Function> fs = new HashSet<>();
            for (Symbol s : st.getSymbols(name)) {
                for (Reference r : getReferencesTo(s.getAddress())) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    if (f != null) fs.add(f);
                    // follow thunks
                    Function t = getFunctionAt(r.getFromAddress());
                    if (t != null && t.isThunk()) for (Reference r2 : getReferencesTo(t.getEntryPoint())) {
                        Function f2 = getFunctionContaining(r2.getFromAddress()); if (f2 != null) fs.add(f2);
                    }
                }
            }
            users.put(name, fs);
            out.println("# " + name + " users: " + fs.size());
        }
        Set<Function> both = new HashSet<>(users.get("_CIsin")); both.addAll(users.get("sin"));
        Set<Function> cos = new HashSet<>(users.get("_CIcos")); cos.addAll(users.get("cos"));
        both.retainAll(cos);
        List<Function> list = new ArrayList<>(both);
        list.sort(Comparator.comparing(f -> f.getEntryPoint()));
        for (Function f : list) out.println(f.getEntryPoint() + " " + f.getName() + " size=" + f.getBody().getNumAddresses());
        out.close();
    }
}
