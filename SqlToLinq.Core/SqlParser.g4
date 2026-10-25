grammar SqlParser;

query : selectQuery SEMICOLON* EOF
      | deleteStmt  SEMICOLON* EOF
      | insertStmt  SEMICOLON* EOF
      | updateStmt  SEMICOLON* EOF
      ;

selectQuery : selectStmt ((UNION ALL? | INTERSECT | EXCEPT) selectStmt)* ;


selectStmt : SELECT DISTINCT? columnList FROM fromClause (WHERE condition)?
			 groupClause? havingClause? orderClause? limitClause? offsetClause? ;

updateStmt : UPDATE tableName SET setClause (WHERE condition)? ;

insertStmt : INSERT INTO tableName ('(' idList ')')? VALUES valueRow (COMMA valueRow)*      # insertValues
           | INSERT INTO tableName ('(' idList ')')? selectQuery                            # insertSelect
           ;

valueRow : '(' valueList ')' ;

deleteStmt : DELETE FROM tableRef (WHERE condition)? ;

fromClause : tableSource joinClause* ;

tableSource : tableRef            # tableRefSource
            | subquery AS? alias  # subquerySource
            ;

tableRef : tableName (AS? alias)? ;

subquery : '(' selectQuery ')'
         | '(' subquery ')'
         ;

parenExprList : '(' exprList ')'
              | '(' parenExprList ')'
              ;

joinClause : joinType? JOIN tableRef ON condition ;

joinType : INNER | LEFT OUTER? | RIGHT OUTER? | CROSS ;

alias : identifier ;



setClause : assignment (COMMA assignment)* ;

assignment : identifier EQ expr ;

columnList : STAR
           | selectItem (COMMA selectItem)* ;

selectItem : expr (AS? identifier)? ;

groupClause : GROUP BY groupItem (COMMA groupItem)* ;

groupItem : identifier (DOT identifier)? ;

idList : identifier (COMMA identifier)* ;

valueList : expr (COMMA expr)* ;

tableName : identifier ;

identifier : IDENTIFIER | QUOTED_IDENTIFIER ;

havingClause : HAVING condition ;

orderClause  : ORDER BY orderItem (COMMA orderItem)* ;

orderItem    : expr (ASC | DESC)? ;

limitClause  :LIMIT expr ;

offsetClause : OFFSET expr ;



condition : '(' condition ')'                                       # parensCondition
          | left=expr op=compOp right=expr                          # compareCondition
          | left=expr NOT? LIKE right=STRING_LITERAL                # likeCondition
          | left=expr NOT? BETWEEN low=expr AND high=expr           # betweenCondition
          | left=expr IS NOT? NULL_TOKEN                            # isNullCondition
          | left=expr NOT? IN subquery                              # inSubqueryCondition
          | left=expr NOT? IN parenExprList                         # inCondition
          | NOT? EXISTS subquery                                    # existsCondition
          | NOT condition                                           # notCondition
          | left=condition AND right=condition                      # andCondition
          | left=condition OR right=condition                       # orCondition
          | expr                                                    # booleanColumnCondition
          ;

exprList : expr (COMMA expr)* ;

expr : left=expr op=mathOp right=expr                           # mathExpr
     | left=expr CONCAT right=expr                              # concatExpr
     | IDENTIFIER '(' DISTINCT expr ')'                         # distinctAggregateExpr
     | IDENTIFIER '(' (STAR | expr) ')'                         # aggregateExpr
     | IDENTIFIER '(' expr (COMMA expr)+ ')'                    # funcCallExpr
     | IDENTIFIER '(' ')'                                       # noArgFuncExpr
     | CURRENT_DATE                                             # currentDateExpr
     | CURRENT_TIMESTAMP                                        # currentTimestampExpr
     | TRUE_TOKEN                                               # trueExpr
     | FALSE_TOKEN                                              # falseExpr
     | CAST '(' expr AS castType ')'                            # castExpr
     | caseExpr                                                 # caseExprAlt
     | subquery                                                 # scalarSubqueryExpr
     | '(' expr ')'                                             # parenExpr
     | identifier DOT identifier                                # qualifiedColumnExpr
     | identifier                                               # columnExpr
     | NUMBER                                                   # numberExpr
     | FLOAT                                                    # floatExpr
     | STRING_LITERAL                                           # stringExpr
     | NULL_TOKEN                                               # nullExpr
     ;

castType : identifier ;

caseExpr : CASE caseOperand=expr? (WHEN condition THEN expr)+ (ELSE elseExpr=expr)? END ;



compOp : EQ | NEQ | GT | LT | GTE | LTE ;
mathOp : PLUS | MINUS | STAR | DIV ;



JOIN    	        : [Jj][Oo][Ii][Nn] ;
INNER   	        : [Ii][Nn][Nn][Ee][Rr] ;
LEFT    	        : [Ll][Ee][Ff][Tt] ;
RIGHT   	        : [Rr][Ii][Gg][Hh][Tt] ;
OUTER  		        : [Oo][Uu][Tt][Ee][Rr] ;
CROSS  	 	        : [Cc][Rr][Oo][Ss][Ss] ;
ON      	        : [Oo][Nn] ;
UPDATE 		        : [Uu][Pp][Dd][Aa][Tt][Ee] ;
SET    		        : [Ss][Ee][Tt] ;
INSERT 		        : [Ii][Nn][Ss][Ee][Rr][Tt] ;
INTO   		        : [Ii][Nn][Tt][Oo] ;
VALUES 		        : [Vv][Aa][Ll][Uu][Ee][Ss] ;
DELETE 		        : [Dd][Ee][Ll][Ee][Tt][Ee] ;
SELECT 		        : [Ss][Ee][Ll][Ee][Cc][Tt] ;
FROM   		        : [Ff][Rr][Oo][Mm] ;
WHERE  		        : [Ww][Hh][Ee][Rr][Ee] ;
AND    		        : [Aa][Nn][Dd] ;
OR     		        : [Oo][Rr] ;
LIKE   		        : [Ll][Ii][Kk][Ee] ;
ORDER  		        : [Oo][Rr][Dd][Ee][Rr] ;
BY    		        : [Bb][Yy] ;
ASC  		        : [Aa][Ss][Cc] ;
DESC   		        : [Dd][Ee][Ss][Cc] ;
HAVING 		        : [Hh][Aa][Vv][Ii][Nn][Gg] ;
AS     		        : [Aa][Ss] ;
CONCAT              : '||' ;
GROUP  		        : [Gg][Rr][Oo][Uu][Pp] ;
BETWEEN 	        : [Bb][Ee][Tt][Ww][Ee][Ee][Nn] ;
NOT     	        : [Nn][Oo][Tt] ;
DISTINCT	        : [Dd][Ii][Ss][Tt][Ii][Nn][Cc][Tt] ;
CASE		        : [Cc][Aa][Ss][Ee] ;
WHEN		        : [Ww][Hh][Ee][Nn] ;
THEN		        : [Tt][Hh][Ee][Nn] ;
ELSE		        : [Ee][Ll][Ss][Ee] ;
END			        : [Ee][Nn][Dd] ;
LIMIT 		        : [Ll][Ii][Mm][Ii][Tt] ;
OFFSET		        : [Oo][Ff][Ff][Ss][Ee][Tt] ;
UNION		        : [Uu][Nn][Ii][Oo][Nn] ;
ALL			        : [Aa][Ll][Ll] ;
INTERSECT	        : [Ii][Nn][Tt][Ee][Rr][Ss][Ee][Cc][Tt] ;
EXISTS		        : [Ee][Xx][Ii][Ss][Tt][Ss] ;
CURRENT_DATE        : [Cc][Uu][Rr][Rr][Ee][Nn][Tt][_][Dd][Aa][Tt][Ee] ;
CURRENT_TIMESTAMP   : [Cc][Uu][Rr][Rr][Ee][Nn][Tt][_][Tt][Ii][Mm][Ee][Ss][Tt][Aa][Mm][Pp] ;
TRUE_TOKEN          : [Tt][Rr][Uu][Ee] ;
FALSE_TOKEN         : [Ff][Aa][Ll][Ss][Ee] ;
EXCEPT		        : [Ee][Xx][Cc][Ee][Pp][Tt] ;
IN      	        : [Ii][Nn] ;
IS      	        : [Ii][Ss] ;
NULL_TOKEN 	        : [Nn][Uu][Ll][Ll] ;
CAST                : [Cc][Aa][Ss][Tt] ;



STAR        : '*' ;
COMMA       : ',' ;
SEMICOLON   : ';' ;
DOT         : '.' ;

EQ          : '=' ;
NEQ         : '<>' ;
GT          : '>' ;
LT          : '<' ;
GTE         : '>=' ;
LTE         : '<=' ;

PLUS        : '+' ;
MINUS       : '-' ;
DIV         : '/' ;

IDENTIFIER     : [a-zA-Z_][a-zA-Z0-9_]* ;


QUOTED_IDENTIFIER   : '"' (~["])+ '"'
                    | '`' (~[`])+ '`'
                    | '[' (~[\]])+ ']'
                    ;

FLOAT          : [0-9]+ '.' [0-9]+ ;
NUMBER         : [0-9]+ ;
STRING_LITERAL : '\'' ~'\''* '\'' ;       

WS : [ \t\r\n]+ -> skip ;
