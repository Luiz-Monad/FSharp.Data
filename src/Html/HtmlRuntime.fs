﻿namespace FSharp.Data.Runtime

open System
open System.ComponentModel
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions
open FSharp.Data
open FSharp.Data.HtmlExtensions
open FSharp.Data.Runtime
open FSharp.Data.Runtime.StructuralTypes

#nowarn "10001"

// --------------------------------------------------------------------------------------

type HtmlTableCell = 
    | Cell of bool * string
    | Empty
    member x.IsHeader =
        match x with
        | Empty -> true
        | Cell(h, _) -> h
    member x.Data = 
        match x with
        | Empty -> ""
        | Cell(_, d) -> d

type HtmlTableRow = 
    | Row of HtmlTableCell [] *  HtmlNode
    | Empty 
    member x.Cells =
        match x with
        | Empty -> [||]
        | Row(cells, _) -> cells

type HtmlTable = 
    { Name : string
      HeaderNamesAndUnits : (string * Type option)[] option // always set at designtime, never at runtime
      InferedProperties : PrimitiveInferedProperty list option // sometimes set at designtime, never at runtime
      HasHeaders: bool option // always set at designtime, never at runtime
      Rows :  HtmlTableRow []
      Html : HtmlNode }
    override x.ToString() =
        let sb = StringBuilder()
        use wr = new StringWriter(sb) 
        wr.WriteLine(x.Name)
        let data = x.Rows
        let rows = data.GetLength(0)
        let columns = (data |> Array.maxBy (fun r -> r.Cells.Length)).Cells.Length
        let widths = Array.zeroCreate columns 
        data |> Array.iteri (fun _ row ->
            row.Cells |> Array.iteri (fun c cell -> widths.[c] <- max (widths.[c]) (cell.Data.Length)))
        for r in 0 .. rows - 1 do
            for c in 0 .. columns - 1 do
                wr.Write(data.[r].Cells.[c].Data.PadRight(widths.[c] + 1))
            wr.WriteLine()
        sb.ToString()

type HtmlList = 
    { Name : string
      Values : string[]
      Html : HtmlNode }

type HtmlDefinitionList = 
    { Name : string
      Definitions : HtmlList list
      Html : HtmlNode }

type HtmlObject = 
    | Table of HtmlTable
    | List of HtmlList
    | DefinitionList of HtmlDefinitionList
    member x.Name =
        match x with
        | Table(t) -> t.Name
        | List(l) -> l.Name
        | DefinitionList(l) -> l.Name

// --------------------------------------------------------------------------------------

/// Helper functions called from the generated code for working with HTML tables
module HtmlRuntime =
    
    let private getName defaultName (element:HtmlNode) (parents:HtmlNode list) = 

        let parents = parents |> Seq.truncate 2 |> Seq.toList

        let tryGetName choices =
            choices
            |> List.tryPick (fun attrName -> 
                element 
                |> HtmlNode.tryGetAttribute attrName
                |> Option.map HtmlAttribute.value
            )

        let rec tryFindPrevious f (x:HtmlNode) (parents:HtmlNode list)= 
            match parents with
            | p::rest ->
                let nearest = 
                    p
                    |> HtmlNode.descendants false true (fun _ -> true)
                    |> Seq.takeWhile ((<>) x) 
                    |> Seq.filter f
                    |> Seq.toList
                    |> List.rev
                match nearest with
                | [] -> tryFindPrevious f p rest
                | h :: _ -> Some h 
            | [] -> None

        let deriveFromSibling element parents = 
            let isHeading s =
                let name = HtmlNode.name s
                Regex.IsMatch(name, """h\d""")
            tryFindPrevious isHeading element parents

        let cleanup (str:String) =
            HtmlParser.wsRegex.Value.Replace(str.Replace('–', '-'), " ").Replace("[edit]", null).Trim()

        match deriveFromSibling element parents with
        | Some e -> cleanup(e.InnerText())
        | _ ->
                match element.Descendants ["caption"] with
                | [] ->
                     match tryGetName ["id"; "name"; "title"; "summary"] with
                     | Some name -> cleanup name
                     | _ -> defaultName
                | h :: _ -> h.InnerText()
                
    let private parseTable inferenceParameters includeLayoutTables makeUnique index (table:HtmlNode, parents:HtmlNode list) = 

        let rows = table.Descendants(["tr"], true, false) |> List.mapi (fun i r -> i,r)
        
        if rows.Length <= 1 then None else

        let cells = rows |> List.map (fun (_,r) -> r.Elements ["td"; "th"] |> List.mapi (fun i e -> i, e))
        let rowLengths = cells |> List.map (fun x -> x.Length)
        let numberOfColumns = List.max rowLengths
        
        if not includeLayoutTables && (numberOfColumns < 1) then None else

        let name = makeUnique (getName (sprintf "Table%d" (index + 1)) table parents)

        let res = Array.init rows.Length (fun _ -> Array.init numberOfColumns (fun _ -> Empty))
        for rowindex, node in rows do
            for colindex, cell in cells.[rowindex] do
                let rowSpan = max 1 (defaultArg (TextConversions.AsInteger CultureInfo.InvariantCulture cell?rowspan) 0) - 1
                let colSpan = max 1 (defaultArg (TextConversions.AsInteger CultureInfo.InvariantCulture cell?colspan) 0) - 1

                let data =
                    let getContents contents = 
                        (contents |> List.map (HtmlNode.innerTextExcluding ["table"; "ul"; "ol"; "sup"; "sub"]) |> String.Concat).Replace(Environment.NewLine, "").Trim()
                    match cell with
                    | HtmlElement("td", _, contents) -> Cell (false, getContents contents)
                    | HtmlElement("th", _, contents) -> Cell (true, getContents contents)
                    | _ -> Empty
                let col_i = ref colindex
                while !col_i < res.[rowindex].Length && res.[rowindex].[!col_i] <> Empty do incr(col_i)
                for j in [!col_i..(!col_i + colSpan)] do
                    for i in [rowindex..(rowindex + rowSpan)] do
                        if i < rows.Length && j < numberOfColumns
                        then res.[i].[j] <- data

        let hasHeaders, headerNamesAndUnits, inferedProperties = 
            match inferenceParameters with
            | None -> None, None, None
            | Some inferenceParameters ->
                let hasHeaders, headerNames, units, inferedProperties = 
                    if res.[0] |> Array.forall (fun r -> r.IsHeader) 
                    then true, res.[0] |> Array.map (fun x -> x.Data) |> Some, None, None
                    else res
                          |> Array.map (Array.map (fun x -> x.Data))
                          |> HtmlInference.inferHeaders inferenceParameters
        
                // headers and units may already be parsed in inferHeaders
                let headerNamesAndUnits =
                  match headerNames, units with
                  | Some headerNames, Some units -> Array.zip headerNames units
                  | _, _ -> CsvInference.parseHeaders headerNames numberOfColumns "" inferenceParameters.UnitsOfMeasureProvider |> fst

                Some hasHeaders, Some headerNamesAndUnits, inferedProperties

        { Name = name
          HeaderNamesAndUnits = headerNamesAndUnits
          InferedProperties = inferedProperties
          HasHeaders = hasHeaders
          Rows = res 
          Html = table } |> Some

    let private parseList makeUnique index (list:HtmlNode, parents:HtmlNode list) =
        
        let rec walkListItems s (items:HtmlNode list) =
            match items with
            | [] -> s
            | HtmlElement("li", _, elements) :: t -> 
                let state = 
                    elements |> List.fold (fun s node ->
                        match node with
                        | HtmlText(content) -> (content.Trim()) :: s
                        | _ -> s
                    ) s
                    |> List.rev
                walkListItems state t
            | _ :: t -> walkListItems s t
            

        let rows = 
            list.Descendants(["li"], false, false) 
            |> List.collect (fun node -> walkListItems [] (node.Descendants((fun _ -> true), true)))
            |> List.toArray
    
        if rows.Length <= 1 then None else

        let name = makeUnique (getName (sprintf "List%d" (index + 1)) list parents)

        { Name = name
          Values = rows
          Html = list } |> Some

    let private parseDefinitionList makeUnique index (definitionList:HtmlNode, parents:HtmlNode list) =
        
        let rec createDefinitionGroups (nodes:HtmlNode list) =
            let rec loop state ((groupName, _, elements) as currentGroup) (nodes:HtmlNode list) =
                match nodes with
                | [] -> (currentGroup :: state) |> List.rev
                | h::t when HtmlNode.name h = "dt" ->
                    loop (currentGroup :: state) (NameUtils.nicePascalName (HtmlNode.innerText h), h, []) t
                | h::t ->
                    loop state (groupName, h, ((HtmlNode.innerText h) :: elements)) t
            match nodes with
            | [] -> []
            | h :: t when HtmlNode.name h = "dt" -> loop [] (NameUtils.nicePascalName (HtmlNode.innerText h), h, []) t
            | h :: t -> loop [] ("Undefined", h, []) t        
        
        let data =
            definitionList.Descendants ["dt"; "dd"]
            |> createDefinitionGroups
            |> List.map (fun (group, node, values) -> { Name = group
                                                        Values = values |> List.rev |> List.toArray
                                                        Html = node })

        if data.Length <= 1 then None else

        let name = makeUnique (getName (sprintf "DefinitionList%d" (index + 1)) definitionList parents)
        
        { Name = name
          Definitions = data
          Html = definitionList } |> Some

    let getTables inferenceParameters includeLayoutTables (doc:HtmlDocument) =
        let tableElements = doc.DescendantsWithPath("table")
        let tableElements = 
            if includeLayoutTables
            then tableElements
            else tableElements |> List.filter (fun (e, _) -> not (e.HasAttribute("cellspacing", "0") && e.HasAttribute("cellpadding", "0")))
        tableElements
        |> List.mapi (parseTable inferenceParameters includeLayoutTables (NameUtils.uniqueGenerator id))
        |> List.choose id

    let getLists (doc:HtmlDocument) =        
        doc
        |> HtmlDocument.descendantsNamedWithPath false ["ol"; "ul"]
        |> List.mapi (parseList (NameUtils.uniqueGenerator id))
        |> List.choose id

    let getDefinitionLists (doc:HtmlDocument) =                
        doc
        |> HtmlDocument.descendantsNamedWithPath false ["dl"]
        |> List.mapi (parseDefinitionList (NameUtils.uniqueGenerator id))
        |> List.choose id

    let getHtmlObjects inferenceParameters includeLayoutTables (doc:HtmlDocument) = 
        (doc |> getTables inferenceParameters includeLayoutTables |> List.map Table) 
        @ (doc |> getLists |> List.map List)
        @ (doc |> getDefinitionLists |> List.map DefinitionList)

type TypedHtmlDocument internal (doc:HtmlDocument, htmlObjects:Map<string,HtmlObject>) =

    member __.Html = doc

    /// [omit]
    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    static member Create(includeLayoutTables, reader:TextReader) =
        let doc = 
            reader 
            |> HtmlDocument.Load
        let htmlObjects = 
            doc
            |> HtmlRuntime.getHtmlObjects None includeLayoutTables
            |> List.map (fun e -> e.Name, e) 
            |> Map.ofList
        TypedHtmlDocument(doc, htmlObjects)

    /// [omit]
    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    member __.GetObject(id:string) = 
        htmlObjects |> Map.find id

type HtmlTableRow<'rowType>(htmlNode)

type HtmlTable<'rowType> internal (name:string, headers:string[] option, values:'rowType[], html:HtmlNode) =

    member __.Name = name
    member __.Headers = headers
    member __.Rows = values
    member __.Html = html

    /// [omit]
    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    static member Create(rowConverter:Func<string[],'rowType>, doc:TypedHtmlDocument, id:string, hasHeaders:bool) =
        match doc.GetObject id with
        | Table table -> 
            let headers, rows = 
                if hasHeaders then
                    Some table.Rows.[0], table.Rows.[1..]
                else
                    None, table.Rows
            HtmlTable<_>(table.Name, headers, Array.map rowConverter.Invoke rows, table.Html)
        | _ -> failwithf "Element %s is not a table" id

type HtmlList<'itemType> internal (name:string, values:'itemType[], html) = 
    
    member __.Name = name
    member __.Values = values
    member __.Html = html

    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    static member Create(rowConverter:Func<string,'itemType>, doc:TypedHtmlDocument, id:string) =
        match doc.GetObject id with
        | List list -> HtmlList<_>(list.Name, Array.map rowConverter.Invoke list.Values, list.Html)
        | _ -> failwithf "Element %s is not a list" id

    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    static member CreateNested(rowConverter:Func<string,'itemType>, doc:TypedHtmlDocument, id:string, index:int) =
        let list = 
            match doc.GetObject id with
            | List list-> list
            | DefinitionList definitionList -> definitionList.Definitions.[index]
            | _ -> failwithf "Element %s is not a list" id
        HtmlList<_>(list.Name, Array.map rowConverter.Invoke list.Values, list.Html)
