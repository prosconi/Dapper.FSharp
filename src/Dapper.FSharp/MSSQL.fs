﻿module Dapper.FSharp.MSSQL

open System.Text
open Dapper.FSharp

module private WhereAnalyzer =
    
    type FieldWhereMetadata = {
        Key : string * WhereComparison
        Name : string
        ParameterName : string
    }
    
    let extractWhereParams (meta:FieldWhereMetadata list) =
        let fn (m:FieldWhereMetadata) =
            match m.Key |> snd with
            | Eq p | Ne p | Gt p
            | Lt p | Ge p | Le p -> (m.ParameterName, p) |> Some
            | In p | NotIn p -> (m.ParameterName, p :> obj) |> Some
            | IsNull | IsNotNull -> None
        meta
        |> List.choose fn

    let rec getWhereMetadata (meta:FieldWhereMetadata list) (w:Where)  =
        match w with
        | Empty -> meta
        | Column (field, comp) ->
            let parName =
                meta
                |> List.filter (fun x -> x.Name = field)
                |> List.length
                |> fun l -> sprintf "Where_%s%i" field (l + 1)
            meta @ [{ Key = (field, comp); Name = field; ParameterName = parName }]
        | Binary(w1, _, w2) -> [w1;w2] |> List.fold getWhereMetadata meta      
    
module private Evaluators =
    
    open System.Linq
    
    let evalCombination = function
        | And -> "AND"
        | Or -> "OR"

    let evalOrderDirection = function
        | Asc -> "ASC"
        | Desc -> "DESC"

    let rec evalWhere (meta:WhereAnalyzer.FieldWhereMetadata list) (w:Where) =
        match w with
        | Empty -> ""
        | Column (field, comp) ->
            let fieldMeta = meta |> List.find (fun x -> x.Key = (field,comp)) 
            let withField op = sprintf "%s %s @%s" fieldMeta.Name op fieldMeta.ParameterName
            match comp with
            | Eq _ -> withField "="
            | Ne _ -> withField "<>"
            | Gt _ -> withField ">"
            | Lt _ -> withField "<"
            | Ge _ -> withField ">="
            | Le _ -> withField "<="
            | In _ -> withField "IN"
            | NotIn _ -> withField "NOT IN"
            | IsNull -> sprintf "%s IS NULL" fieldMeta.Name
            | IsNotNull -> sprintf "%s IS NOT NULL" fieldMeta.Name
        | Binary(w1, comb, w2) ->
            match evalWhere meta w1, evalWhere meta w2 with
            | "", fq | fq , "" -> fq
            | fq1, fq2 -> sprintf "(%s %s %s)" fq1 (evalCombination comb) fq2

    let evalOrderBy (xs:OrderBy list) =
        xs
        |> List.map (fun (n,s) -> sprintf "%s %s" n (evalOrderDirection s))
        |> String.concat ", "

    let evalPagination (pag:Pagination) =
        match pag with
        | Skip x when x <= 0 -> ""
        | Skip o -> sprintf "OFFSET %i ROWS" o
        | SkipTake(o,f) -> sprintf "OFFSET %i ROWS FETCH NEXT %i ROWS ONLY" o f
    
    let evalJoins (joins:Join list) =
        let sb = StringBuilder()
        let evalJoin = function
            | InnerJoin(table,colName,equalsTo) -> sprintf " INNER JOIN %s ON %s.%s=%s" table table colName equalsTo
            | LeftJoin(table,colName,equalsTo) -> sprintf " LEFT JOIN %s ON %s.%s=%s" table table colName equalsTo
        joins |> List.map evalJoin |> List.iter (sb.Append >> ignore)
        sb.ToString()
    
    let evalSelectQuery fields meta (q:SelectQuery) =
        let fieldNames = fields |> List.filter (fun x -> not <| q.IgnoredColumns.Contains(x)) |> String.concat ", "
        // basic query
        let sb = StringBuilder(sprintf "SELECT %s FROM %s" fieldNames q.Table)
        // joins
        let joins = evalJoins q.Joins
        if joins.Length > 0 then sb.Append joins |> ignore
        // where
        let where = evalWhere meta q.Where
        if where.Length > 0 then sb.Append (sprintf " WHERE %s" where) |> ignore
        // order by
        let orderBy = evalOrderBy q.OrderBy
        if orderBy.Length > 0 then sb.Append (sprintf " ORDER BY %s" orderBy) |> ignore
        // pagination
        let pagination = evalPagination q.Pagination
        if pagination.Length > 0 then sb.Append (sprintf " %s" pagination) |> ignore    
        sb.ToString()
            
    let evalInsertQuery fields (q:InsertQuery<_>) =
        let fieldNames = fields |> String.concat ", " |> sprintf "(%s)"
        let values = fields |> List.map (sprintf "@%s") |> String.concat ", " |> sprintf "(%s)" 
        sprintf "INSERT INTO %s %s VALUES %s" q.Table fieldNames values        

    let evalUpdateQuery fields meta (q:UpdateQuery<'a>) =
        // basic query
        let pairs = fields |> List.map (fun x -> sprintf "%s=@%s" x x) |> String.concat ", "
        let sb = StringBuilder(sprintf "UPDATE %s SET %s" q.Table pairs)
        // where
        let where = evalWhere meta q.Where
        if where.Length > 0 then sb.Append (sprintf " WHERE %s" where) |> ignore
        sb.ToString()

    let evalDeleteQuery meta (q:DeleteQuery) =
        // basic query
        let sb = StringBuilder(sprintf "DELETE FROM %s" q.Table)
        // where
        let where = evalWhere meta q.Where
        if where.Length > 0 then sb.Append (sprintf " WHERE %s" where) |> ignore
        sb.ToString()        
    
module private Reflection =        

    let getFields<'a> () =
        FSharp.Reflection.FSharpType.GetRecordFields(typeof<'a>)
        |> Array.map (fun x -> x.Name)
        |> Array.toList
    
    let getValues r =
        FSharp.Reflection.FSharpValue.GetRecordFields r
        |> Array.toList
        
module private Preparators =
    
    let prepareSelect<'a> (q:SelectQuery) =
        let fields = Reflection.getFields<'a>()
        // extract metadata
        let meta = WhereAnalyzer.getWhereMetadata [] q.Where
        let query = Evaluators.evalSelectQuery fields meta q
        let pars = WhereAnalyzer.extractWhereParams meta |> Map.ofList
        query, pars
    
    let prepareSelectTuple2<'a,'b> (q:SelectQuery) =
        let tableTwo = q.Joins.Head |> Join.tableName
        let fieldsOne = Reflection.getFields<'a>() |> List.map (sprintf "%s.%s" q.Table)
        let fieldsTwo = Reflection.getFields<'b>() |> List.map (sprintf "%s.%s" tableTwo)
        let fields = fieldsOne @ fieldsTwo
        
        // extract metadata
        let meta = WhereAnalyzer.getWhereMetadata [] q.Where
        let query = Evaluators.evalSelectQuery fields meta q
        let pars = WhereAnalyzer.extractWhereParams meta |> Map.ofList
        query, pars
        
    let prepareInsert (q:InsertQuery<'a>) =
        let fields = Reflection.getFields<'a>()
        let query = Evaluators.evalInsertQuery fields q
        query, q.Values
    
    let prepareUpdate<'a> (q:UpdateQuery<'a>) =
        let fields = Reflection.getFields<'a>()
        let values = Reflection.getValues q.Value
        // extract metadata
        let meta = WhereAnalyzer.getWhereMetadata [] q.Where
        let pars = (WhereAnalyzer.extractWhereParams meta) @ (List.zip fields values) |> Map.ofList
        let query = Evaluators.evalUpdateQuery fields meta q
        query, pars
        
    let prepareDelete (q:DeleteQuery) =
        let meta = WhereAnalyzer.getWhereMetadata [] q.Where
        let pars = (WhereAnalyzer.extractWhereParams meta) |> Map.ofList
        let query = Evaluators.evalDeleteQuery meta q
        query, pars

open Dapper

type System.Data.IDbConnection with
    member this.SelectAsync<'a> (q:SelectQuery) =
        let query, pars = q |> Preparators.prepareSelect<'a>
        //printfn "%s" query
        this.QueryAsync<'a>(query, pars)
    
    member this.SelectAsync<'a,'b> (q:SelectQuery) =
        let query, pars = q |> Preparators.prepareSelectTuple2<'a,'b>
        //printfn "%s" query
        this.QueryAsync<'a,'b,('a * 'b)>(query, (fun x y -> x, y), pars)
        
    member this.InsertAsync<'a> (q:InsertQuery<'a>) =
        let query, values = q |> Preparators.prepareInsert
        this.ExecuteAsync(query, values)
        
    member this.UpdateAsync<'a> (q:UpdateQuery<'a>) =
        let query, pars = q |> Preparators.prepareUpdate<'a>
        this.ExecuteAsync(query, pars)
    
    member this.DeleteAsync (q:DeleteQuery) =
        let query, pars = q |> Preparators.prepareDelete
        this.ExecuteAsync(query, pars)