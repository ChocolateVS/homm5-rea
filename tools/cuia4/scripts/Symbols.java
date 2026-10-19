// Lists symbols whose full name matches a regex. Usage: -postScript Symbols.java <outFile> <regex>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.*;
import java.io.*;
public class Symbols extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        java.util.regex.Pattern p = java.util.regex.Pattern.compile(a[1]);
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(true)) {
            String n = s.getName(true);
            if (p.matcher(n).find()) out.println(s.getAddress() + " " + n);
        }
        out.close();
    }
}
